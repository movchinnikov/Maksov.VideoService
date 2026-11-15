using Maksov.VideoService.Videos.MediaWorker.Adapters.Http;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.MediaWorker.Features.Outbox;

/// <summary>
/// Delivers events from the outbox to subscribers: wakes on NOTIFY (fallback polling is rare), takes a batch,
/// publishes, marks as sent. On failure it retries with growing backoff (5 s … 5 min): the event is not lost,
/// and an unavailable subscriber is not hammered.
/// </summary>
internal sealed partial class OutboxDispatcher(
    IOutbox outbox,
    IEventPublisher publisher,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var poll = TimeSpan.FromSeconds(o.PollSeconds);
        var subscribers = o.ConfiguredSubscribers();
        IJobSignal? signal = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    signal ??= await outbox.ListenAsync(stoppingToken).ConfigureAwait(false);
                    var batch = await outbox.ClaimAsync(subscribers, o.BatchSize, ClaimLease, stoppingToken).ConfigureAwait(false);
                    if (batch.Count == 0)
                    {
                        await signal.WaitAsync(poll, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var message in batch)
                    {
                        await DeliverAsync(message, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or IOException or TimeoutException)
                {
                    LogOutboxUnavailable(logger, ex);
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
            if (signal is not null)
            {
                await signal.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task DeliverAsync(OutboxMessage message, CancellationToken ct)
    {
        try
        {
            await publisher.PublishAsync(message, ct).ConfigureAwait(false);
            await outbox.MarkSentAsync(message.Id, message.Subscriber, ct).ConfigureAwait(false);
            LogDelivered(logger, message.Topic, message.Subscriber, message.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            var delay = Backoff(message.Attempt);
            LogDeliveryFailed(logger, ex, message.Topic, message.Subscriber, message.Id, message.Attempt, delay.TotalSeconds);
            await outbox.MarkFailedAsync(message.Id, message.Subscriber, ex.Message, delay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 6))));

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivered {Topic} to {Subscriber}: {EventId}")]
    private static partial void LogDelivered(ILogger logger, string topic, string subscriber, Guid eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delivery of {Topic} to {Subscriber} ({EventId}) failed (attempt {Attempt}), retry in {Seconds}s")]
    private static partial void LogDeliveryFailed(ILogger logger, Exception ex, string topic, string subscriber, Guid eventId, int attempt, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox unavailable, retrying")]
    private static partial void LogOutboxUnavailable(ILogger logger, Exception ex);
}
