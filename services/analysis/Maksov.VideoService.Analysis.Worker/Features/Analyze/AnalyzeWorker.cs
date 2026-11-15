using Maksov.VideoService.Analysis.Bll.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Analysis.Worker.Features.Analyze;

/// <summary>
/// Worker loop: waits for NOTIFY (or a fallback timeout), picks up jobs while there are any,
/// and runs at most MaxConcurrentJobs at a time. No empty loops.
/// </summary>
internal sealed partial class AnalyzeWorker(
    IAnalysisQueue queue,
    AnalyzeHandler handler,
    Heartbeat heartbeat,
    IOptions<AnalysisOptions> options,
    ILogger<AnalyzeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var lease = TimeSpan.FromSeconds(o.LeaseSeconds);
        var poll = TimeSpan.FromSeconds(o.PollSeconds);
        Directory.CreateDirectory(o.WorkDirectory);

        using var slots = new SemaphoreSlim(o.MaxConcurrentJobs, o.MaxConcurrentJobs);
        var running = new List<Task>(o.MaxConcurrentJobs);
        IJobSignal? signal = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                heartbeat.Beat();
                try
                {
                    signal ??= await queue.ListenAsync(stoppingToken).ConfigureAwait(false);
                    await slots.WaitAsync(stoppingToken).ConfigureAwait(false);

                    var job = await queue.TryDequeueAsync(lease, stoppingToken).ConfigureAwait(false);
                    if (job is null)
                    {
                        slots.Release();
                        await signal.WaitAsync(poll, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    running.RemoveAll(static t => t.IsCompleted);
                    running.Add(RunAsync(job, slots, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or TimeoutException)
                {
                    // DB unavailable: reconnect with a pause, without loading it.
                    LogQueueUnavailable(logger, ex);
                    if (signal is not null)
                    {
                        await signal.DisposeAsync().ConfigureAwait(false);
                        signal = null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(running).ConfigureAwait(false);
            if (signal is not null)
            {
                await signal.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task RunAsync(AnalysisJob job, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await handler.HandleAsync(job, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Could not even write the result (DB unavailable): the job returns to work when the lease expires.
            LogJobCrashed(logger, ex, job.VideoId);
        }
        finally
        {
            slots.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis job {VideoId} crashed; it will be retried after lease expiry")]
    private static partial void LogJobCrashed(ILogger logger, Exception ex, Guid videoId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job queue unavailable, retrying")]
    private static partial void LogQueueUnavailable(ILogger logger, Exception ex);
}
