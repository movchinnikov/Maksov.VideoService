using System.Buffers;
using System.Text.Json;
using Maksov.VideoService.Likes.Bll.Ports;

namespace Maksov.VideoService.Likes.Bll;

/// <summary>An outbound event ready for the outbox.</summary>
public sealed record OutboundEvent(Guid EventId, string Topic, string PayloadJson);

/// <summary>
/// Builds the outbound like events (contracts: contracts/events/video-liked.v1.json, video-unliked.v1.json,
/// comment-liked.v1.json, comment-unliked.v1.json). Camel-case JSON, written by hand: no reflection.
/// </summary>
public static class LikeEvents
{
    public const int SchemaVersion = 1;

    /// <summary>The only subscriber of the like events for now; rows wait in the outbox until its address is configured.</summary>
    public static IReadOnlyCollection<string> Subscribers { get; } = ["lake"];

    public static string TopicFor(LikeTargetType type, bool liked) => (type, liked) switch
    {
        (LikeTargetType.Video, true) => "video.liked.v1",
        (LikeTargetType.Video, false) => "video.unliked.v1",
        (LikeTargetType.Comment, true) => "comment.liked.v1",
        _ => "comment.unliked.v1",
    };

    public static OutboundEvent Build(LikeTarget target, string userId, bool liked, DateTimeOffset occurredAt, string? traceId)
    {
        ArgumentNullException.ThrowIfNull(target);
        var eventId = Guid.CreateVersion7();
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("eventId", eventId);
            w.WriteString("occurredAt", occurredAt.UtcDateTime);
            w.WriteNumber("schemaVersion", SchemaVersion);
            if (string.IsNullOrEmpty(traceId))
            {
                w.WriteNull("traceId");
            }
            else
            {
                w.WriteString("traceId", traceId);
            }

            if (target.Type == LikeTargetType.Video)
            {
                w.WriteString("videoId", target.Id);
                w.WriteString("ownerId", target.OwnerId);
            }
            else
            {
                w.WriteString("commentId", target.Id);
                w.WriteString("videoId", target.VideoId);
                w.WriteString("commentAuthorId", target.OwnerId);
            }

            w.WriteString("userId", userId);
            w.WriteEndObject();
        }

        return new OutboundEvent(eventId, TopicFor(target.Type, liked), System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }
}
