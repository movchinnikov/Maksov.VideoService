using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.MediaWorker.Features.Transcode;

/// <summary>
/// Worker loop: waits for NOTIFY (or a fallback timeout), takes jobs while there are any,
/// and runs at most MaxConcurrentJobs at once. No empty cycles.
/// </summary>
internal sealed partial class TranscodeWorker(
    IJobQueue queue,
    TranscodeHandler handler,
    Heartbeat heartbeat,
    IOptions<TranscodeOptions> options,
    ILogger<TranscodeWorker> logger) : BackgroundService
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
                    signal ??= await queue.ListenAsync(stoppingToken);
                    await slots.WaitAsync(stoppingToken);

                    var job = await queue.TryDequeueAsync(lease, stoppingToken);
                    if (job is null)
                    {
                        slots.Release();
                        await signal.WaitAsync(poll, stoppingToken);
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
                    // DB unavailable: reconnect with a delay, without hammering it.
                    LogQueueUnavailable(logger, ex);
                    if (signal is not null)
                    {
                        await signal.DisposeAsync();
                        signal = null;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(running);
            if (signal is not null)
            {
                await signal.DisposeAsync();
            }
        }
    }

    private async Task RunAsync(TranscodeJob job, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            await handler.HandleAsync(job, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Could not even record the result (DB unavailable): the job returns to work when the lease expires.
            LogJobCrashed(logger, ex, job.Id);
        }
        finally
        {
            slots.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {JobId} crashed; it will be retried after lease expiry")]
    private static partial void LogJobCrashed(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Job queue unavailable, retrying")]
    private static partial void LogQueueUnavailable(ILogger logger, Exception ex);
}

