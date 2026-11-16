using System.Text.Json;
using Maksov.VideoService.Likes.Bll;
using Maksov.VideoService.Likes.Bll.Ports;

namespace Maksov.VideoService.Likes.Tests;

/// <summary>Outbound payloads match the contracts in contracts/events/*-liked|unliked.v1.json.</summary>
public sealed class LikeEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, "video.liked.v1")]
    [InlineData(false, "video.unliked.v1")]
    public void Video_event_has_contract_fields(bool liked, string topic)
    {
        var videoId = Guid.CreateVersion7();
        var target = new LikeTarget(LikeTargetType.Video, videoId, videoId, "owner-1");

        var e = LikeEvents.Build(target, "alice", liked, Now, "trace-1");
        using var doc = JsonDocument.Parse(e.PayloadJson);
        var root = doc.RootElement;

        Assert.Equal(topic, e.Topic);
        Assert.Equal(e.EventId, root.GetProperty("eventId").GetGuid());
        Assert.Equal(7, e.EventId.Version);
        Assert.Equal(Now, root.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("trace-1", root.GetProperty("traceId").GetString());
        Assert.Equal(videoId, root.GetProperty("videoId").GetGuid());
        Assert.Equal("owner-1", root.GetProperty("ownerId").GetString());
        Assert.Equal("alice", root.GetProperty("userId").GetString());
        Assert.False(root.TryGetProperty("commentId", out _));
    }

    [Theory]
    [InlineData(true, "comment.liked.v1")]
    [InlineData(false, "comment.unliked.v1")]
    public void Comment_event_has_contract_fields_and_null_trace_when_absent(bool liked, string topic)
    {
        var commentId = Guid.CreateVersion7();
        var videoId = Guid.CreateVersion7();
        var target = new LikeTarget(LikeTargetType.Comment, commentId, videoId, "author-1");

        var e = LikeEvents.Build(target, "alice", liked, Now, null);
        using var doc = JsonDocument.Parse(e.PayloadJson);
        var root = doc.RootElement;

        Assert.Equal(topic, e.Topic);
        Assert.Equal(commentId, root.GetProperty("commentId").GetGuid());
        Assert.Equal(videoId, root.GetProperty("videoId").GetGuid());
        Assert.Equal("author-1", root.GetProperty("commentAuthorId").GetString());
        Assert.Equal("alice", root.GetProperty("userId").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("traceId").ValueKind);
        Assert.False(root.TryGetProperty("ownerId", out _));
    }

    [Fact]
    public void Every_event_gets_a_new_event_id()
    {
        var target = new LikeTarget(LikeTargetType.Video, Guid.CreateVersion7(), Guid.CreateVersion7(), "o");

        Assert.NotEqual(LikeEvents.Build(target, "a", true, Now, null).EventId, LikeEvents.Build(target, "a", true, Now, null).EventId);
    }

    [Fact]
    public void Liker_cursor_round_trips_and_rejects_garbage()
    {
        var cursor = new LikerCursor(Now, "user-1");

        var decoded = LikerCursor.TryDecode(cursor.Encode());

        Assert.Equal(cursor, decoded);
        Assert.Null(LikerCursor.TryDecode("!!!"));
        Assert.Null(LikerCursor.TryDecode(null));
        Assert.Null(LikerCursor.TryDecode(new string('A', 700)));
    }
}
