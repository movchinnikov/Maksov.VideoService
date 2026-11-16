using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maksov.VideoService.Likes.Bll;
using Maksov.VideoService.Likes.Bll.Ports;
using Maksov.VideoService.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maksov.VideoService.Likes.Tests;

public sealed class LikesFactory : WebApplicationFactory<Program>
{
    public const string InternalKey = "test-internal-key";

    public InMemoryLikes Likes { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=unused;Database=unused");
        builder.UseSetting("Internal:ApiKey", InternalKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILikeRepository>();
            services.AddSingleton<ILikeRepository>(Likes);
            services.AddTestAuthentication();
        });
    }

    public HttpClient ClientFor(string userId) => CreateClient().ClientFor(userId);
}

/// <summary>In-memory port: same observable behaviour as the Postgres adapter (idempotency, tombstones, cascade, outbox topics).</summary>
public sealed class InMemoryLikes : ILikeRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, (string Owner, bool Deleted)> _videos = [];
    private readonly Dictionary<Guid, (Guid VideoId, string Author, bool Deleted)> _comments = [];
    private readonly Dictionary<(LikeTargetType Type, Guid Id, string User), (Guid VideoId, DateTimeOffset At)> _likes = [];

    public ConcurrentQueue<string> Topics { get; } = new();

    public Task UpsertVideoAsync(Guid videoId, string ownerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _videos.TryAdd(videoId, (ownerId, false));
        }

        return Task.CompletedTask;
    }

    public Task ForgetVideoAsync(Guid videoId, string? ownerId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _videos[videoId] = (ownerId ?? string.Empty, true);
            foreach (var key in _likes.Where(l => l.Value.VideoId == videoId).Select(l => l.Key).ToList())
            {
                _likes.Remove(key);
            }

            foreach (var id in _comments.Where(c => c.Value.VideoId == videoId).Select(c => c.Key).ToList())
            {
                _comments.Remove(id);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetVideoOwnerAsync(Guid videoId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_videos.TryGetValue(videoId, out var v) && !v.Deleted ? v.Owner : null);
        }
    }

    public Task UpsertCommentAsync(Guid commentId, Guid videoId, string authorId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!(_videos.TryGetValue(videoId, out var v) && v.Deleted))
            {
                _comments.TryAdd(commentId, (videoId, authorId, false));
            }
        }

        return Task.CompletedTask;
    }

    public Task ForgetCommentAsync(Guid commentId, Guid videoId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _comments[commentId] = (videoId, _comments.TryGetValue(commentId, out var c) ? c.Author : string.Empty, true);
            foreach (var key in _likes.Keys.Where(k => k.Type == LikeTargetType.Comment && k.Id == commentId).ToList())
            {
                _likes.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task<KnownComment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_comments.TryGetValue(commentId, out var c) && !c.Deleted ? new KnownComment(commentId, c.VideoId, c.Author) : null);
        }
    }

    public Task<bool> AddLikeAsync(LikeTarget target, string userId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_likes.TryAdd((target.Type, target.Id, userId), (target.VideoId, DateTimeOffset.UtcNow)))
            {
                return Task.FromResult(false);
            }

            Topics.Enqueue(LikeEvents.TopicFor(target.Type, liked: true));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RemoveLikeAsync(LikeTarget target, string userId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_likes.Remove((target.Type, target.Id, userId)))
            {
                return Task.FromResult(false);
            }

            Topics.Enqueue(LikeEvents.TopicFor(target.Type, liked: false));
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<LikeCount>> GetCountsAsync(LikeTargetType type, IReadOnlyCollection<Guid> ids, string userId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<LikeCount> result = [.. ids.Select(id => new LikeCount(
                id,
                _likes.Keys.Count(k => k.Type == type && k.Id == id),
                _likes.ContainsKey((type, id, userId))))];
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<Liker>> ListLikersAsync(Guid videoId, LikerCursor? after, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<Liker> result = [.. _likes
                .Where(l => l.Key.Type == LikeTargetType.Video && l.Key.Id == videoId)
                .Select(l => new Liker(l.Key.User, l.Value.At))
                .Where(l => after is null || l.LikedAt < after.LikedAt || (l.LikedAt == after.LikedAt && string.CompareOrdinal(l.UserId, after.UserId) < 0))
                .OrderByDescending(l => l.LikedAt).ThenByDescending(l => l.UserId, StringComparer.Ordinal)
                .Take(limit)];
            return Task.FromResult(result);
        }
    }

    public Task<int> CountVideoLikesAsync(Guid videoId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_likes.Keys.Count(k => k.Type == LikeTargetType.Video && k.Id == videoId));
        }
    }
}

public sealed class LikesApiTests(LikesFactory factory) : IClassFixture<LikesFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> KnownVideoAsync(string owner)
    {
        var id = Guid.CreateVersion7();
        await factory.Likes.UpsertVideoAsync(id, owner, Ct);
        return id;
    }

    private async Task<Guid> KnownCommentAsync(Guid videoId, string author)
    {
        var id = Guid.CreateVersion7();
        await factory.Likes.UpsertCommentAsync(id, videoId, author, Ct);
        return id;
    }

    private static HttpRequestMessage Internal(string path, object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("X-Internal-Key", key);
        }

        return request;
    }

    private static async Task<(int Count, bool LikedByMe)> CountAsync(HttpClient client, string kind, Guid id)
    {
        var body = await client.GetFromJsonAsync<JsonElement>($"/api/likes/{kind}?ids={id}", Json, Ct);
        var item = body.GetProperty("items").EnumerateArray().Single();
        return (item.GetProperty("count").GetInt32(), item.GetProperty("likedByMe").GetBoolean());
    }

    [Fact]
    public async Task AC_likes_01_requires_authentication()
    {
        using var client = factory.CreateClient();

        using var put = await client.PutAsync($"/api/videos/{Guid.CreateVersion7()}/like", null, Ct);
        using var counts = await client.GetAsync($"/api/likes/videos?ids={Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, counts.StatusCode);
    }

    [Fact]
    public async Task AC_likes_02_unknown_targets_are_404()
    {
        using var client = factory.ClientFor("alice");
        var video = Guid.CreateVersion7();
        var known = await KnownVideoAsync("bob");

        using var likeVideo = await client.PutAsync($"/api/videos/{video}/like", null, Ct);
        using var unlikeVideo = await client.DeleteAsync($"/api/videos/{video}/like", Ct);
        using var likeComment = await client.PutAsync($"/api/videos/{known}/comments/{Guid.CreateVersion7()}/like", null, Ct);
        using var likers = await client.GetAsync($"/api/videos/{video}/likes", Ct);

        Assert.Equal(HttpStatusCode.NotFound, likeVideo.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unlikeVideo.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, likeComment.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, likers.StatusCode);
    }

    [Fact]
    public async Task AC_likes_03_like_and_unlike_video_are_idempotent()
    {
        using var alice = factory.ClientFor("alice");
        var id = await KnownVideoAsync("bob");
        var before = factory.Likes.Topics.Count;

        using var p1 = await alice.PutAsync($"/api/videos/{id}/like", null, Ct);
        using var p2 = await alice.PutAsync($"/api/videos/{id}/like", null, Ct);
        var afterLike = await CountAsync(alice, "videos", id);
        using var d1 = await alice.DeleteAsync($"/api/videos/{id}/like", Ct);
        using var d2 = await alice.DeleteAsync($"/api/videos/{id}/like", Ct);
        var afterUnlike = await CountAsync(alice, "videos", id);

        Assert.All(new[] { p1, p2, d1, d2 }, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal((1, true), afterLike);
        Assert.Equal((0, false), afterUnlike);
        Assert.Equal(["video.liked.v1", "video.unliked.v1"], factory.Likes.Topics.Skip(before));
    }

    [Fact]
    public async Task AC_likes_04_comment_like_requires_matching_video()
    {
        using var alice = factory.ClientFor("alice");
        var video = await KnownVideoAsync("bob");
        var otherVideo = await KnownVideoAsync("bob");
        var comment = await KnownCommentAsync(video, "carol");

        using var wrongVideo = await alice.PutAsync($"/api/videos/{otherVideo}/comments/{comment}/like", null, Ct);
        using var ok1 = await alice.PutAsync($"/api/videos/{video}/comments/{comment}/like", null, Ct);
        using var ok2 = await alice.PutAsync($"/api/videos/{video}/comments/{comment}/like", null, Ct);
        var liked = await CountAsync(alice, "comments", comment);
        using var unlike = await alice.DeleteAsync($"/api/videos/{video}/comments/{comment}/like", Ct);
        var unliked = await CountAsync(alice, "comments", comment);

        Assert.Equal(HttpStatusCode.NotFound, wrongVideo.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ok1.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ok2.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unlike.StatusCode);
        Assert.Equal((1, true), liked);
        Assert.Equal((0, false), unliked);
    }

    [Fact]
    public async Task AC_likes_05_batch_counts_show_liked_by_me_and_zero_for_unknown()
    {
        var a = await KnownVideoAsync("bob");
        var b = await KnownVideoAsync("bob");
        var unknown = Guid.CreateVersion7();
        using var alice = factory.ClientFor("alice");
        using var carol = factory.ClientFor("carol");
        (await alice.PutAsync($"/api/videos/{a}/like", null, Ct)).Dispose();
        (await carol.PutAsync($"/api/videos/{a}/like", null, Ct)).Dispose();
        (await carol.PutAsync($"/api/videos/{b}/like", null, Ct)).Dispose();

        var body = await alice.GetFromJsonAsync<JsonElement>($"/api/likes/videos?ids={a},{b},{unknown},{a}", Json, Ct);
        var items = body.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([a, b, unknown], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([2, 1, 0], items.Select(i => i.GetProperty("count").GetInt32()));
        Assert.Equal([true, false, false], items.Select(i => i.GetProperty("likedByMe").GetBoolean()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    public async Task AC_likes_06_bad_ids_are_rejected(string ids)
    {
        using var alice = factory.ClientFor("alice");

        using var response = await alice.GetAsync($"/api/likes/videos?ids={ids}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC_likes_07_more_than_50_ids_are_rejected()
    {
        using var alice = factory.ClientFor("alice");
        var ids = string.Join(',', Enumerable.Range(0, 51).Select(_ => Guid.CreateVersion7()));

        using var tooMany = await alice.GetAsync($"/api/likes/videos?ids={ids}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task AC_likes_08_likers_are_paged_newest_first_with_total()
    {
        var id = await KnownVideoAsync("bob");
        using var viewer = factory.ClientFor("viewer");
        var users = new[] { "u1", "u2", "u3" };
        foreach (var user in users)
        {
            using var client = factory.ClientFor(user);
            (await client.PutAsync($"/api/videos/{id}/like", null, Ct)).Dispose();
            await Task.Delay(5, Ct);
        }

        var page1 = await viewer.GetFromJsonAsync<JsonElement>($"/api/videos/{id}/likes?limit=2", Json, Ct);
        var cursor = page1.GetProperty("nextCursor").GetString();
        var page2 = await viewer.GetFromJsonAsync<JsonElement>($"/api/videos/{id}/likes?limit=2&cursor={cursor}", Json, Ct);

        Assert.Equal(["u3", "u2"], page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetString()));
        Assert.Equal(["u1"], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetString()));
        Assert.Equal(3, page1.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task AC_likes_09_internal_routes_require_the_key()
    {
        using var client = factory.CreateClient();
        var body = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId = Guid.CreateVersion7(), ownerId = "bob" };

        using var none = await client.SendAsync(Internal("/internal/v1/events/video-processed", body, null), Ct);
        using var wrong = await client.SendAsync(Internal("/internal/v1/events/video-processed", body, "wrong"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task AC_likes_10_event_intake_is_idempotent_and_deletion_is_not_resurrected()
    {
        using var client = factory.CreateClient();
        using var alice = factory.ClientFor("alice");
        var videoId = Guid.CreateVersion7();
        var commentId = Guid.CreateVersion7();
        var processed = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId, ownerId = "bob" };
        var added = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, commentId, videoId, authorId = "carol", videoOwnerId = "bob", textLength = 5 };
        var deleted = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId, ownerId = "bob" };

        using var p1 = await client.SendAsync(Internal("/internal/v1/events/video-processed", processed, LikesFactory.InternalKey), Ct);
        using var p2 = await client.SendAsync(Internal("/internal/v1/events/video-processed", processed, LikesFactory.InternalKey), Ct);
        using var c1 = await client.SendAsync(Internal("/internal/v1/events/comment-added", added, LikesFactory.InternalKey), Ct);
        using var c2 = await client.SendAsync(Internal("/internal/v1/events/comment-added", added, LikesFactory.InternalKey), Ct);
        using var likeVideo = await alice.PutAsync($"/api/videos/{videoId}/like", null, Ct);
        using var likeComment = await alice.PutAsync($"/api/videos/{videoId}/comments/{commentId}/like", null, Ct);
        using var d1 = await client.SendAsync(Internal("/internal/v1/events/video-deleted", deleted, LikesFactory.InternalKey), Ct);
        using var d2 = await client.SendAsync(Internal("/internal/v1/events/video-deleted", deleted, LikesFactory.InternalKey), Ct);
        using var lateVideo = await client.SendAsync(Internal("/internal/v1/events/video-processed", processed, LikesFactory.InternalKey), Ct);
        using var lateComment = await client.SendAsync(Internal("/internal/v1/events/comment-added", added, LikesFactory.InternalKey), Ct);
        using var afterVideo = await alice.PutAsync($"/api/videos/{videoId}/like", null, Ct);
        using var afterComment = await alice.PutAsync($"/api/videos/{videoId}/comments/{commentId}/like", null, Ct);

        Assert.All(new[] { p1, p2, c1, c2, d1, d2, lateVideo, lateComment }, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(HttpStatusCode.NoContent, likeVideo.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, likeComment.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterVideo.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, afterComment.StatusCode);
    }

    [Fact]
    public async Task AC_likes_11_comment_deleted_removes_the_comment_and_blocks_a_late_added()
    {
        using var client = factory.CreateClient();
        using var alice = factory.ClientFor("alice");
        var videoId = await KnownVideoAsync("bob");
        var commentId = await KnownCommentAsync(videoId, "carol");
        var deleted = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, commentId, videoId, authorId = "carol", deletedBy = "bob" };
        var added = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, commentId, videoId, authorId = "carol", videoOwnerId = "bob", textLength = 1 };

        using var d1 = await client.SendAsync(Internal("/internal/v1/events/comment-deleted", deleted, LikesFactory.InternalKey), Ct);
        using var d2 = await client.SendAsync(Internal("/internal/v1/events/comment-deleted", deleted, LikesFactory.InternalKey), Ct);
        using var late = await client.SendAsync(Internal("/internal/v1/events/comment-added", added, LikesFactory.InternalKey), Ct);
        using var like = await alice.PutAsync($"/api/videos/{videoId}/comments/{commentId}/like", null, Ct);

        Assert.All(new[] { d1, d2, late }, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, like.StatusCode);
    }

    [Fact]
    public async Task AC_likes_12_invalid_events_are_rejected()
    {
        using var client = factory.CreateClient();
        var id = Guid.CreateVersion7();

        using var wrongSchema = await client.SendAsync(Internal("/internal/v1/events/video-processed",
            new { eventId = id, occurredAt = DateTimeOffset.UtcNow, schemaVersion = 2, videoId = id, ownerId = "bob" }, LikesFactory.InternalKey), Ct);
        using var noOwner = await client.SendAsync(Internal("/internal/v1/events/video-processed",
            new { eventId = id, occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId = id, ownerId = "" }, LikesFactory.InternalKey), Ct);
        using var noVideo = await client.SendAsync(Internal("/internal/v1/events/video-deleted",
            new { eventId = id, occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId = Guid.Empty }, LikesFactory.InternalKey), Ct);
        using var noAuthor = await client.SendAsync(Internal("/internal/v1/events/comment-added",
            new { eventId = id, occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, commentId = id, videoId = id, textLength = 1 }, LikesFactory.InternalKey), Ct);
        using var noComment = await client.SendAsync(Internal("/internal/v1/events/comment-deleted",
            new { eventId = id, occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, commentId = Guid.Empty, videoId = id }, LikesFactory.InternalKey), Ct);

        Assert.All(new[] { wrongSchema, noOwner, noVideo, noAuthor, noComment }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }
}
