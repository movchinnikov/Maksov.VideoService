namespace Maksov.VideoService.BuildingBlocks.Outbox;

/// <summary>
/// Outbox dispatcher settings (section <c>Outbox</c>). Subscribers are configured as
/// <c>Outbox__Subscribers__&lt;name&gt;__BaseUrl</c>; rows of subscribers without an address stay pending (a backlog that is
/// delivered as soon as the subscriber is configured).
/// </summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public Dictionary<string, OutboxSubscriberOptions> Subscribers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int BatchSize { get; set; } = 20;

    /// <summary>Fallback wake-up when no NOTIFY arrives, seconds.</summary>
    public int PollSeconds { get; set; } = 30;

    public IReadOnlyList<string> ConfiguredSubscribers() =>
        [.. Subscribers.Where(static s => !string.IsNullOrWhiteSpace(s.Value.BaseUrl)).Select(static s => s.Key)];
}

public sealed class OutboxSubscriberOptions
{
    public string BaseUrl { get; set; } = "";
}
