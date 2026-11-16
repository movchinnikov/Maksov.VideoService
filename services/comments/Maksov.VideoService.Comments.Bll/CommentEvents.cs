using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Maksov.VideoService.Comments.Bll;

/// <summary>
/// Domain events of the comments service (contracts: contracts/events/comment-added.v1.json and comment-deleted.v1.json).
/// The comment text is never published: the lake gets only its length.
/// </summary>
public static class CommentEvents
{
    public const string AddedTopic = "comment.added.v1";
    public const string DeletedTopic = "comment.deleted.v1";
    public const string Likes = "likes";
    public const string Lake = "lake";
    public const int SchemaVersion = 1;

    private static readonly string[] Subscribers = [Likes, Lake];

    /// <summary>Subscribers of both comment events (one outbox row each).</summary>
    public static IReadOnlyCollection<string> SubscribersOf(string topic) => topic switch
    {
        AddedTopic or DeletedTopic => Subscribers,
        _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, "Unknown event topic"),
    };

    public static string AddedPayload(Guid eventId, DateTimeOffset occurredAt, Guid commentId, Guid videoId, string authorId, string videoOwnerId, int textLength) =>
        Build(eventId, occurredAt, w =>
        {
            w.WriteString("commentId", commentId);
            w.WriteString("videoId", videoId);
            w.WriteString("authorId", authorId);
            w.WriteString("videoOwnerId", videoOwnerId);
            w.WriteNumber("textLength", textLength);
        });

    public static string DeletedPayload(Guid eventId, DateTimeOffset occurredAt, Guid commentId, Guid videoId, string authorId, string deletedBy) =>
        Build(eventId, occurredAt, w =>
        {
            w.WriteString("commentId", commentId);
            w.WriteString("videoId", videoId);
            w.WriteString("authorId", authorId);
            w.WriteString("deletedBy", deletedBy);
        });

    private static string Build(Guid eventId, DateTimeOffset occurredAt, Action<Utf8JsonWriter> writeExtra)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("eventId", eventId);
            w.WriteString("occurredAt", occurredAt);
            w.WriteNumber("schemaVersion", SchemaVersion);
            var traceId = Activity.Current?.Id;
            if (traceId is null)
            {
                w.WriteNull("traceId");
            }
            else
            {
                w.WriteString("traceId", traceId);
            }

            writeExtra(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
