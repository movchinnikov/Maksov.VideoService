namespace Maksov.VideoService.Videos.MediaWorker.Ports;

/// <summary>An event for a single subscriber. An outbox row is an (event, subscriber) pair: each has its own delivery.</summary>
public sealed record OutboxMessage(Guid Id, string Subscriber, string Topic, string Payload, int Attempt);

/// <summary>Outbox port: reliable delivery of video events. Postgres table + NOTIFY; in lesson 4 publishing moves to Kafka.</summary>
public interface IOutbox
{
    Task<IJobSignal> ListenAsync(CancellationToken cancellationToken);

    /// <summary>Takes a batch of unsent events for the given subscribers and locks them for <paramref name="lease"/>.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(IReadOnlyCollection<string> subscribers, int limit, TimeSpan lease, CancellationToken cancellationToken);

    Task MarkSentAsync(Guid id, string subscriber, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string subscriber, string details, TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Port for delivering an event to a subscriber. Throws if delivery fails.</summary>
public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
