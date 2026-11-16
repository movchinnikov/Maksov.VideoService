namespace Maksov.VideoService.BuildingBlocks.Outbox;

/// <summary>An event for a single subscriber. An outbox row is an (event, subscriber) pair: each has its own delivery.</summary>
public sealed record OutboxMessage(Guid Id, string Subscriber, string Topic, string Payload, int Attempt);
