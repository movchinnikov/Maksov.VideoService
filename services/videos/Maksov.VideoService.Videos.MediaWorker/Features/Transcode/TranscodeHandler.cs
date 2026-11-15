using System.Diagnostics;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Domain;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.MediaWorker.Features.Transcode;

/// <summary>
/// Use case "Transcode video": source → validation → CMAF ABR ladder (DASH + HLS) → poster → MinIO → Ready.
/// Idempotent: keys are deterministic, Ready is set only after all files are uploaded,
/// a retry for an already ready video is a no-op.
/// </summary>
internal sealed partial class TranscodeHandler(
    IJobQueue queue,
    IObjectStorage storage,
    IMediaProbe probe,
    ITranscoder transcoder,
    Heartbeat heartbeat,
    WorkerMetrics metrics,
    IOptions<TranscodeOptions> options,
    ILogger<TranscodeHandler> logger)
{
    private const int UploadParallelism = 4;
    private readonly TranscodeOptions _o = options.Value;

    public async Task HandleAsync(TranscodeJob job, CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        var lease = TimeSpan.FromSeconds(_o.LeaseSeconds);
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = leaseLost.Token;
        var keeper = KeepLeaseAsync(job, lease, leaseLost);
        var workDir = Path.Combine(_o.WorkDirectory, job.VideoId.ToString("N"));
        var outcome = "failed";

        try
        {
            if (job.Attempt > job.MaxAttempts)
            {
                await queue.FailAsync(job, "max_attempts", "Attempts exhausted", CancellationToken.None);
                return;
            }

            var sourceKey = await queue.StartProcessingAsync(job, ct);
            if (sourceKey is null)
            {
                await queue.SkipAsync(job, ct);
                outcome = "skipped";
                return;
            }

            LogStarted(logger, job.VideoId, job.Attempt);
            ResetDirectory(workDir);
            var outDir = Directory.CreateDirectory(Path.Combine(workDir, "stream")).FullName;
            var sourcePath = Path.Combine(workDir, "source");

            await storage.DownloadToFileAsync(sourceKey, sourcePath, ct);
            if (new FileInfo(sourcePath).Length > _o.MaxInputBytes)
            {
                throw new MediaRejectedException("file_too_large", "Input exceeds size limit");
            }

            var media = await probe.ProbeAsync(sourcePath, ct);
            MediaPolicy.EnsureAcceptable(media, _o);

            var ladder = RenditionLadder.Plan(media.Width, media.Height, media.FrameRate);
            var request = new TranscodeRequest(
                sourcePath, outDir, ladder, media.HasSampleAspectRatio, media.HasAudio, RenditionLadder.AudioKbps(ladder), media.DurationSeconds,
                CapFrameRate: media.FrameRate > 60, _o.SegmentSeconds, _o.Threads, _o.Preset);

            var progress = new Progress<double>(p => LogProgress(logger, job.VideoId, (int)(p * 100)));
            await transcoder.TranscodeAsync(request, progress, ct);

            var posterPath = Path.Combine(workDir, MediaKeys.PosterFile);
            await transcoder.CapturePosterAsync(sourcePath, posterPath, Math.Min(1.0, media.DurationSeconds / 2), ct);

            await UploadAsync(job.VideoId, outDir, posterPath, ct);

            var top = ladder[^1];
            var published = await queue.CompleteAsync(job, new ProcessedVideo((int)(media.DurationSeconds * 1000), top.Width, top.Height, ladder), ct);
            if (!published)
            {
                // The video was deleted during processing: remove whatever was already uploaded.
                await storage.DeletePrefixAsync(MediaKeys.VideoPrefix(job.VideoId), CancellationToken.None);
                outcome = "deleted";
                LogDeletedDuringProcessing(logger, job.VideoId);
                return;
            }

            outcome = "ready";
            var elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            LogReady(logger, job.VideoId, ladder.Count, elapsedSeconds);
        }
        catch (MediaRejectedException ex)
        {
            outcome = "rejected";
            LogRejected(logger, job.VideoId, ex.ReasonCode, ex.Message);
            await queue.FailAsync(job, ex.ReasonCode, ex.Message, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            outcome = "released";
            await queue.ReleaseAsync(job, CancellationToken.None);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            outcome = "lease_lost"; // another worker took the job — write nothing
            LogLeaseLost(logger, job.VideoId);
        }
        catch (Exception ex)
        {
            outcome = "retry";
            LogFailed(logger, ex, job.VideoId, job.Attempt);
            var delay = TimeSpan.FromSeconds(30 * Math.Pow(2, job.Attempt - 1));
            if (job.Attempt >= job.MaxAttempts)
            {
                await queue.FailAsync(job, "transcode_error", ex.Message, CancellationToken.None);
            }
            else
            {
                await queue.RetryAsync(job, ex.Message, delay, CancellationToken.None);
            }
        }
        finally
        {
            await leaseLost.CancelAsync();
            await keeper;
            TryDelete(workDir);
            metrics.JobFinished(outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task KeepLeaseAsync(TranscodeJob job, TimeSpan lease, CancellationTokenSource leaseLost)
    {
        using var timer = new PeriodicTimer(lease / 3);
        try
        {
            while (await timer.WaitForNextTickAsync(leaseLost.Token))
            {
                heartbeat.Beat();
                if (!await queue.ExtendLeaseAsync(job, lease, leaseLost.Token))
                {
                    await leaseLost.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Npgsql.NpgsqlException ex)
        {
            // Could not extend — continue; if the lease expires, the job will be re-taken and processed idempotently.
            LogLeaseExtendFailed(logger, ex, job.VideoId);
        }
    }

    private async Task UploadAsync(Guid videoId, string outDir, string posterPath, CancellationToken ct)
    {
        // Segments first, then manifests: the player never sees a manifest referencing files not yet uploaded.
        var files = Directory.EnumerateFiles(outDir)
            .OrderBy(static f => MediaKeys.IsManifest(f) ? 1 : 0)
            .ToList();

        var segments = files.Where(static f => !MediaKeys.IsManifest(f)).ToList();
        var manifests = files.Where(MediaKeys.IsManifest).ToList();

        var parallel = new ParallelOptions { MaxDegreeOfParallelism = UploadParallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(segments, parallel, (file, token) => UploadOneAsync(videoId, file, token));
        await Parallel.ForEachAsync(manifests, parallel, (file, token) => UploadOneAsync(videoId, file, token));
        await storage.UploadFileAsync(MediaKeys.Poster(videoId), posterPath, "image/jpeg", ct);
    }

    private async ValueTask UploadOneAsync(Guid videoId, string file, CancellationToken ct)
    {
        var name = Path.GetFileName(file);
        var contentType = MediaKeys.ContentTypeFor(name) ?? "application/octet-stream";
        await storage.UploadFileAsync(MediaKeys.StreamFile(videoId, name), file, contentType, ct);
    }

    private static void ResetDirectory(string path)
    {
        TryDelete(path);
        Directory.CreateDirectory(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcoding {VideoId}, attempt {Attempt}")]
    private static partial void LogStarted(ILogger logger, Guid videoId, int attempt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Transcoding {VideoId}: {Percent}%")]
    private static partial void LogProgress(ILogger logger, Guid videoId, int percent);

    [LoggerMessage(Level = LogLevel.Information, Message = "Video {VideoId} was deleted during processing; result discarded")]
    private static partial void LogDeletedDuringProcessing(ILogger logger, Guid videoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Video {VideoId} ready: {Renditions} renditions in {Seconds:F1}s")]
    private static partial void LogReady(ILogger logger, Guid videoId, int renditions, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Video {VideoId} rejected: {Reason} ({Details})")]
    private static partial void LogRejected(ILogger logger, Guid videoId, string reason, string details);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease lost for {VideoId}, abandoning")]
    private static partial void LogLeaseLost(ILogger logger, Guid videoId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to extend lease for {VideoId}")]
    private static partial void LogLeaseExtendFailed(ILogger logger, Exception ex, Guid videoId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Transcoding {VideoId} failed on attempt {Attempt}")]
    private static partial void LogFailed(ILogger logger, Exception ex, Guid videoId, int attempt);
}
