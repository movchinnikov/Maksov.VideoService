namespace Maksov.VideoService.Videos.Domain;

/// <summary>
/// Video service events and their subscribers. Contracts: contracts/events/*.json.
/// There is no broker yet (lesson 4: Kafka), so an event is written to the outbox as a separate row per subscriber
/// and delivered over internal HTTP; each subscriber has its own delivery, retries and backoff.
/// </summary>
public static class VideoEvents
{
    public const string Processed = "video.processed.v1";
    public const string Deleted = "video.deleted.v1";

    public const string Analysis = "analysis";
    public const string Comments = "comments";
    public const string Likes = "likes";

    /// <summary>Whom the event is addressed to.</summary>
    public static IReadOnlyList<string> SubscribersOf(string topic) => topic switch
    {
        Processed => [Analysis, Comments, Likes],
        Deleted => [Analysis, Comments, Likes],
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown event topic"),
    };

    /// <summary>Internal path where the subscriber receives the event.</summary>
    public static string PathOf(string topic) => topic switch
    {
        Processed => "/internal/v1/events/video-processed",
        Deleted => "/internal/v1/events/video-deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown event topic"),
    };
}
