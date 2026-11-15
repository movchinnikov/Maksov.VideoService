using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.Worker.Domain;
using Maksov.VideoService.Analysis.Worker.Ports;
using Maksov.VideoService.BuildingBlocks.Storage;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Analysis.Worker.Features.Analyze;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VideoFacts))]
internal sealed partial class FactsJsonContext : JsonSerializerContext;

/// <summary>
/// Use case "Analyze a video": source from MinIO → ffmpeg "readings" → (optional) frame captions from the model →
/// text and tags → save. Idempotent: the result is written with a single command by video_id, a retry overwrites it with the same one.
/// An analysis failure does not affect the video in any way — the viewer just will not see a description.
/// </summary>
internal sealed partial class AnalyzeHandler(
    IAnalysisQueue queue,
    IObjectStorage storage,
    IVideoInspector inspector,
    IFrameCaptioner captioner,
    Heartbeat heartbeat,
    IOptions<AnalysisOptions> options,
    ILogger<AnalyzeHandler> logger)
{
    private readonly AnalysisOptions _o = options.Value;

    public async Task HandleAsync(AnalysisJob job, CancellationToken stoppingToken)
    {
        var lease = TimeSpan.FromSeconds(_o.LeaseSeconds);
        using var leaseLost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = leaseLost.Token;
        var keeper = KeepLeaseAsync(job, lease, leaseLost);
        var workDir = Path.Combine(_o.WorkDirectory, job.VideoId.ToString("N"));

        try
        {
            if (job.Attempt > job.MaxAttempts)
            {
                await queue.FailAsync(job, "max_attempts", "Attempts exhausted", CancellationToken.None).ConfigureAwait(false);
                return;
            }

            LogStarted(logger, job.VideoId, job.Attempt);
            ResetDirectory(workDir);
            var sourcePath = Path.Combine(workDir, "source");
            await storage.DownloadToFileAsync(job.SourceKey, sourcePath, ct).ConfigureAwait(false);

            var facts = await inspector.InspectAsync(sourcePath, job, ct).ConfigureAwait(false);
            var caption = await captioner.CaptionAsync(sourcePath, job, ct).ConfigureAwait(false);
            var (text, tags) = DescriptionComposer.Compose(facts, caption);

            await queue.CompleteAsync(job, new AnalysisResult(text, tags, JsonSerializer.Serialize(facts, FactsJsonContext.Default.VideoFacts)), ct)
                .ConfigureAwait(false);
            LogDone(logger, job.VideoId, tags.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await queue.ReleaseAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            LogLeaseLost(logger, job.VideoId); // another worker took the job — write nothing
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex, job.VideoId, job.Attempt);
            if (job.Attempt >= job.MaxAttempts)
            {
                await queue.FailAsync(job, "analysis_error", ex.Message, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await queue.RetryAsync(job, ex.Message, TimeSpan.FromSeconds(30 * Math.Pow(2, job.Attempt - 1)), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await leaseLost.CancelAsync().ConfigureAwait(false);
            await keeper.ConfigureAwait(false);
            TryDelete(workDir);
        }
    }

    private async Task KeepLeaseAsync(AnalysisJob job, TimeSpan lease, CancellationTokenSource leaseLost)
    {
        using var timer = new PeriodicTimer(lease / 3);
        try
        {
            while (await timer.WaitForNextTickAsync(leaseLost.Token).ConfigureAwait(false))
            {
                heartbeat.Beat();
                if (!await queue.ExtendLeaseAsync(job, lease, leaseLost.Token).ConfigureAwait(false))
                {
                    await leaseLost.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Npgsql.NpgsqlException ex)
        {
            // Could not renew — continue; if the lease expires, the job will be re-taken and processed idempotently.
            LogLeaseExtendFailed(logger, ex, job.VideoId);
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Analyzing {VideoId}, attempt {Attempt}")]
    private static partial void LogStarted(ILogger logger, Guid videoId, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Description for {VideoId} ready ({Tags} tags)")]
    private static partial void LogDone(ILogger logger, Guid videoId, int tags);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease lost for {VideoId}, abandoning")]
    private static partial void LogLeaseLost(ILogger logger, Guid videoId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to extend lease for {VideoId}")]
    private static partial void LogLeaseExtendFailed(ILogger logger, Exception ex, Guid videoId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis of {VideoId} failed on attempt {Attempt}")]
    private static partial void LogFailed(ILogger logger, Exception ex, Guid videoId, int attempt);
}
