using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maksov.VideoService.Comments.Bll;
using Maksov.VideoService.Comments.Bll.Ports;
using Maksov.VideoService.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maksov.VideoService.Comments.Tests;

public sealed class CommentsFactory : WebApplicationFactory<Program>
{
    public const string InternalKey = "test-internal-key";

    public InMemoryComments Comments { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=unused;Database=unused");
        builder.UseSetting("Internal:ApiKey", InternalKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICommentRepository>();
            services.AddSingleton<ICommentRepository>(Comments);
            services.AddTestAuthentication();
        });
    }

    public HttpClient ClientFor(string userId) => CreateClient().ClientFor(userId);
}

public sealed class InMemoryComments : ICommentRepository
{
    private readonly ConcurrentDictionary<Guid, (string Owner, bool Deleted)> _videos = new();
    private readonly ConcurrentDictionary<Guid, CommentRecord> _comments = new();

    /// <summary>Events the real adapter would write to the outbox (topic and payload).</summary>
    public ConcurrentQueue<(string Topic, string Payload)> Events { get; } = new();

    public Task UpsertVideoAsync(Guid videoId, string ownerId, CancellationToken cancellationToken)
    {
        _videos.TryAdd(videoId, (ownerId, false));
        return Task.CompletedTask;
    }

    public Task ForgetVideoAsync(Guid videoId, string? ownerId, CancellationToken cancellationToken)
    {
        _videos[videoId] = (ownerId ?? string.Empty, true);
        foreach (var c in _comments.Values.Where(c => c.VideoId == videoId))
        {
            _comments.TryRemove(c.Id, out _);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetVideoOwnerAsync(Guid videoId, CancellationToken cancellationToken) =>
        Task.FromResult(_videos.TryGetValue(videoId, out var v) && !v.Deleted ? v.Owner : null);

    public Task InsertAsync(NewComment comment, string videoOwnerId, CancellationToken cancellationToken)
    {
        Events.Enqueue((CommentEvents.AddedTopic, CommentEvents.AddedPayload(
            Guid.CreateVersion7(), comment.CreatedAt, comment.Id, comment.VideoId, comment.AuthorId, videoOwnerId, comment.Text.Length)));
        _comments[comment.Id] = new CommentRecord(comment.Id, comment.VideoId, comment.AuthorId, comment.AuthorUsername, comment.Text, comment.CreatedAt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CommentRecord>> ListAsync(Guid videoId, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<CommentRecord> list = _comments.Values
            .Where(c => c.VideoId == videoId)
            .Where(c => after is null || c.CreatedAt < after.CreatedAt || (c.CreatedAt == after.CreatedAt && c.Id.CompareTo(after.Id) < 0))
            .OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id)
            .Take(limit)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<int> CountAsync(Guid videoId, CancellationToken cancellationToken) =>
        Task.FromResult(_comments.Values.Count(c => c.VideoId == videoId));

    public Task<CommentRecord?> GetAsync(Guid videoId, Guid commentId, CancellationToken cancellationToken) =>
        Task.FromResult(_comments.TryGetValue(commentId, out var c) && c.VideoId == videoId ? c : null);

    public Task<bool> DeleteAsync(Guid videoId, Guid commentId, string deletedBy, CancellationToken cancellationToken)
    {
        if (!_comments.TryRemove(commentId, out var removed))
        {
            return Task.FromResult(false);
        }

        Events.Enqueue((CommentEvents.DeletedTopic, CommentEvents.DeletedPayload(
            Guid.CreateVersion7(), DateTimeOffset.UtcNow, commentId, videoId, removed.AuthorId, deletedBy)));
        return Task.FromResult(true);
    }
}

public sealed class CommentsApiTests(CommentsFactory factory) : IClassFixture<CommentsFactory>
{
    private static readonly string[] AddedThenDeleted = [CommentEvents.AddedTopic, CommentEvents.DeletedTopic];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> KnownVideoAsync(string owner)
    {
        var id = Guid.CreateVersion7();
        await factory.Comments.UpsertVideoAsync(id, owner, Ct);
        return id;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, Guid videoId, string text)
    {
        using var response = await client.PostAsJsonAsync($"/api/videos/{videoId}/comments", new { text }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
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

    [Fact]
    public async Task AC_comments_01_requires_authentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/videos/{Guid.CreateVersion7()}/comments", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC_comments_02_unknown_video_is_404()
    {
        using var client = factory.ClientFor("alice");
        var id = Guid.CreateVersion7();

        using var list = await client.GetAsync($"/api/videos/{id}/comments", Ct);
        using var post = await client.PostAsJsonAsync($"/api/videos/{id}/comments", new { text = "hello" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AC_comments_03_empty_text_is_rejected(string text)
    {
        using var client = factory.ClientFor("alice");
        var id = await KnownVideoAsync("bob");

        using var response = await client.PostAsJsonAsync($"/api/videos/{id}/comments", new { text }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC_comments_04_too_long_text_is_rejected()
    {
        using var client = factory.ClientFor("alice");
        var id = await KnownVideoAsync("bob");

        using var ok = await client.PostAsJsonAsync($"/api/videos/{id}/comments", new { text = new string('x', 500) }, Ct);
        using var tooLong = await client.PostAsJsonAsync($"/api/videos/{id}/comments", new { text = new string('x', 501) }, Ct);

        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task AC_comments_05_list_is_paged_newest_first_with_total_and_can_delete()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var id = await KnownVideoAsync(owner);
        using var alice = factory.ClientFor("alice");
        using var bob = factory.ClientFor("bob");
        var first = (await PostAsync(alice, id, "first")).GetProperty("id").GetGuid();
        await Task.Delay(5, Ct);
        var second = (await PostAsync(bob, id, "second")).GetProperty("id").GetGuid();
        await Task.Delay(5, Ct);
        var third = (await PostAsync(alice, id, "third")).GetProperty("id").GetGuid();

        var page1 = await alice.GetFromJsonAsync<JsonElement>($"/api/videos/{id}/comments?limit=2", Json, Ct);
        var cursor = page1.GetProperty("nextCursor").GetString();
        var page2 = await alice.GetFromJsonAsync<JsonElement>($"/api/videos/{id}/comments?limit=2&cursor={cursor}", Json, Ct);
        var items1 = page1.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([third, second], items1.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([first], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(3, page1.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
        Assert.True(items1[0].GetProperty("canDelete").GetBoolean());   // third is mine
        Assert.False(items1[1].GetProperty("canDelete").GetBoolean());  // second is Bob's
    }

    [Fact]
    public async Task AC_comments_06_author_and_video_owner_can_delete_others_get_404()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var id = await KnownVideoAsync(owner);
        using var alice = factory.ClientFor("alice");
        using var mallory = factory.ClientFor("mallory");
        using var videoOwner = factory.ClientFor(owner);
        var byAlice = (await PostAsync(alice, id, "mine")).GetProperty("id").GetGuid();
        var byMallory = (await PostAsync(mallory, id, "spam")).GetProperty("id").GetGuid();

        using var foreign = await mallory.DeleteAsync($"/api/videos/{id}/comments/{byAlice}", Ct);
        using var own = await alice.DeleteAsync($"/api/videos/{id}/comments/{byAlice}", Ct);
        using var byOwner = await videoOwner.DeleteAsync($"/api/videos/{id}/comments/{byMallory}", Ct);
        using var again = await alice.DeleteAsync($"/api/videos/{id}/comments/{byAlice}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Added_and_deleted_comments_emit_events_without_the_text()
    {
        var owner = $"owner-{Guid.NewGuid():N}";
        var id = await KnownVideoAsync(owner);
        using var alice = factory.ClientFor("alice");
        var commentId = (await PostAsync(alice, id, "a secret opinion")).GetProperty("id").GetGuid();
        using var deleted = await alice.DeleteAsync($"/api/videos/{id}/comments/{commentId}", Ct);
        using var again = await alice.DeleteAsync($"/api/videos/{id}/comments/{commentId}", Ct);

        var mine = factory.Comments.Events.Where(e => e.Payload.Contains(commentId.ToString(), StringComparison.Ordinal)).ToList();

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(AddedThenDeleted, mine.Select(e => e.Topic));
        Assert.All(mine, e => Assert.DoesNotContain("secret", e.Payload, StringComparison.Ordinal));
        var added = JsonElement.Parse(mine[0].Payload);
        Assert.Equal(owner, added.GetProperty("videoOwnerId").GetString());
        Assert.Equal(16, added.GetProperty("textLength").GetInt32());
        Assert.Equal("alice", JsonElement.Parse(mine[1].Payload).GetProperty("deletedBy").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task AC_comments_07_internal_events_require_the_key(string? key)
    {
        using var client = factory.CreateClient();
        var body = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId = Guid.CreateVersion7(), ownerId = "bob" };

        using var processed = await client.SendAsync(Internal("/internal/v1/events/video-processed", body, key), Ct);
        using var deleted = await client.SendAsync(Internal("/internal/v1/events/video-deleted", body, key), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, processed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, deleted.StatusCode);
    }

    [Fact]
    public async Task AC_comments_08_events_register_and_forget_the_video_idempotently()
    {
        using var client = factory.CreateClient();
        using var alice = factory.ClientFor("alice");
        var videoId = Guid.CreateVersion7();
        var processed = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId, ownerId = "bob" };
        var deleted = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId, ownerId = "bob" };

        using var p1 = await client.SendAsync(Internal("/internal/v1/events/video-processed", processed, CommentsFactory.InternalKey), Ct);
        using var p2 = await client.SendAsync(Internal("/internal/v1/events/video-processed", processed, CommentsFactory.InternalKey), Ct);
        await PostAsync(alice, videoId, "before deletion");
        using var d1 = await client.SendAsync(Internal("/internal/v1/events/video-deleted", deleted, CommentsFactory.InternalKey), Ct);
        using var d2 = await client.SendAsync(Internal("/internal/v1/events/video-deleted", deleted, CommentsFactory.InternalKey), Ct);
        using var late = await client.SendAsync(Internal("/internal/v1/events/video-processed", processed, CommentsFactory.InternalKey), Ct);
        using var list = await alice.GetAsync($"/api/videos/{videoId}/comments", Ct);

        Assert.All(new[] { p1, p2, d1, d2, late }, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
    }

    [Fact]
    public async Task AC_comments_09_invalid_events_are_rejected()
    {
        using var client = factory.CreateClient();

        using var wrongSchema = await client.SendAsync(Internal("/internal/v1/events/video-processed",
            new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 2, videoId = Guid.CreateVersion7(), ownerId = "bob" }, CommentsFactory.InternalKey), Ct);
        using var noOwner = await client.SendAsync(Internal("/internal/v1/events/video-processed",
            new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId = Guid.CreateVersion7(), ownerId = "" }, CommentsFactory.InternalKey), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, wrongSchema.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noOwner.StatusCode);
    }
}
