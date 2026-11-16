using System.Net;
using System.Net.Http.Json;

namespace Maksov.VideoService.Gateway.GraphQL.Tests;

public sealed class GraphQLTests : IClassFixture<GraphQLFactory>
{
    private const string V1 = "0190f5b4-0000-7000-8000-000000000001";
    private const string V2 = "0190f5b4-0000-7000-8000-000000000002";
    private const string C1 = "0190f5b4-0000-7000-8000-0000000000c1";

    private readonly GraphQLFactory _factory;

    public GraphQLTests(GraphQLFactory factory)
    {
        _factory = factory;
        _factory.ResetStubs();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Card(string id, string ownerId, string title) => new
    {
        id,
        title,
        ownerId,
        ownerUsername = ownerId,
        createdAt = "2026-11-17T10:00:00Z",
        durationMs = 5000,
        width = 720,
        height = 1280,
        playback = new { dash = "/d", hls = "/h", poster = "/p" },
    };

    [Fact]
    public async Task AC_graphql_01_anonymous_request_is_rejected_before_any_service()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/graphql", new { query = "{ me { id } }" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.All(_factory.All, s => Assert.Empty(s.Calls));
    }

    [Fact]
    public async Task AC_graphql_02_feed_is_composed_with_one_downstream_call_per_service()
    {
        _factory.Feed.On("GET", "/api/feed", new StubReply(200, new { ids = new[] { V1, V2 }, nextCursor = "next" }));
        _factory.Videos.On("GET", "/api/videos/cards", new StubReply(200, new { items = new[] { Card(V1, "alice", "First"), Card(V2, "bob", "Second") } }));
        _factory.Users.On("GET", "/api/users", new StubReply(200, new
        {
            items = new[]
            {
                new { id = "alice", username = "alice", displayName = "Alice", bio = "" },
                new { id = "bob", username = "bob", displayName = "Bob", bio = "" },
            },
        }));
        _factory.Likes.On("GET", "/api/likes/videos", new StubReply(200, new
        {
            items = new[] { new { id = V1, count = 3, likedByMe = true }, new { id = V2, count = 0, likedByMe = false } },
        }));
        using var client = _factory.ClientFor("alice", "token-123");

        var result = await GraphQLFactory.ExecuteAsync(
            client,
            "{ feed(limit: 2) { nextCursor items { id title owner { username displayName } likes { count likedByMe } } } }",
            Ct);

        Assert.False(result.TryGetProperty("errors", out _), result.ToString());
        var feed = result.GetProperty("data").GetProperty("feed");
        Assert.Equal("next", feed.GetProperty("nextCursor").GetString());
        var items = feed.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("First", items[0].GetProperty("title").GetString());
        Assert.Equal("Alice", items[0].GetProperty("owner").GetProperty("displayName").GetString());
        Assert.Equal(3, items[0].GetProperty("likes").GetProperty("count").GetInt32());
        Assert.True(items[0].GetProperty("likes").GetProperty("likedByMe").GetBoolean());
        Assert.Equal("bob", items[1].GetProperty("owner").GetProperty("username").GetString());

        // DataLoader batching: exactly one request per service for the whole screen.
        Assert.Single(_factory.Feed.Calls);
        Assert.Single(_factory.Videos.Calls);
        Assert.Single(_factory.Users.Calls);
        Assert.Single(_factory.Likes.Calls);
        Assert.Contains("ids=" + V1 + "," + V2, _factory.Videos.Calls.Single().Query, StringComparison.Ordinal);
        Assert.Contains("alice", _factory.Users.Calls.Single().Query, StringComparison.Ordinal);
        Assert.Contains("bob", _factory.Users.Calls.Single().Query, StringComparison.Ordinal);

        // The caller's token is forwarded to every service (zero trust).
        Assert.All(_factory.All.SelectMany(s => s.Calls), c => Assert.Equal("Bearer token-123", c.Authorization));
    }

    [Fact]
    public async Task AC_graphql_03_friend_stats_are_deduplicated_per_user()
    {
        _factory.Users.On("GET", "/api/users/me", new StubReply(200, new { id = "alice", username = "alice", displayName = "Alice", bio = "", createdAt = "2026-11-17T10:00:00Z" }));
        _factory.Friends.On("GET", "/api/friends/alice/stats", new StubReply(200, new { followersCount = 4, followingCount = 2, isFollowing = false, followsYou = false, isMutual = false }));
        using var client = _factory.ClientFor("alice");

        var result = await GraphQLFactory.ExecuteAsync(client, "{ me { id stats { followersCount } same: stats { followingCount } } }", Ct);

        Assert.False(result.TryGetProperty("errors", out _), result.ToString());
        Assert.Equal(4, result.GetProperty("data").GetProperty("me").GetProperty("stats").GetProperty("followersCount").GetInt32());
        Assert.Single(_factory.Friends.Calls);
    }

    [Fact]
    public async Task AC_graphql_04_like_video_mutation_calls_likes_and_returns_fresh_counters()
    {
        _factory.Likes.On("PUT", $"/api/videos/{V1}/like", new StubReply(204));
        _factory.Likes.On("GET", "/api/likes/videos", new StubReply(200, new { items = new[] { new { id = V1, count = 8, likedByMe = true } } }));
        using var client = _factory.ClientFor("alice");

        var result = await GraphQLFactory.ExecuteAsync(client, $"mutation {{ likeVideo(videoId: \"{V1}\") {{ count likedByMe }} }}", Ct);

        Assert.False(result.TryGetProperty("errors", out _), result.ToString());
        var like = result.GetProperty("data").GetProperty("likeVideo");
        Assert.Equal(8, like.GetProperty("count").GetInt32());
        Assert.True(like.GetProperty("likedByMe").GetBoolean());
        Assert.Equal(1, _factory.Likes.CountOf("PUT", $"/api/videos/{V1}/like"));
    }

    [Fact]
    public async Task AC_graphql_05_comment_mutations_reach_the_comments_and_likes_services()
    {
        _factory.Comments.On("POST", $"/api/videos/{V1}/comments", new StubReply(201, new
        {
            id = C1,
            videoId = V1,
            authorId = "alice",
            authorUsername = "alice",
            text = "nice",
            createdAt = "2026-11-17T10:00:00Z",
            canDelete = true,
        }));
        _factory.Comments.On("DELETE", $"/api/videos/{V1}/comments/{C1}", new StubReply(204));
        _factory.Likes.On("PUT", $"/api/videos/{V1}/comments/{C1}/like", new StubReply(204));
        _factory.Likes.On("GET", "/api/likes/comments", new StubReply(200, new { items = new[] { new { id = C1, count = 1, likedByMe = true } } }));
        using var client = _factory.ClientFor("alice");

        var added = await GraphQLFactory.ExecuteAsync(client, $"mutation {{ addComment(videoId: \"{V1}\", text: \"nice\") {{ id text canDelete }} }}", Ct);
        var liked = await GraphQLFactory.ExecuteAsync(client, $"mutation {{ likeComment(videoId: \"{V1}\", commentId: \"{C1}\") {{ count }} }}", Ct);
        var deleted = await GraphQLFactory.ExecuteAsync(client, $"mutation {{ deleteComment(videoId: \"{V1}\", commentId: \"{C1}\") }}", Ct);

        Assert.Equal("nice", added.GetProperty("data").GetProperty("addComment").GetProperty("text").GetString());
        Assert.Equal(1, liked.GetProperty("data").GetProperty("likeComment").GetProperty("count").GetInt32());
        Assert.True(deleted.GetProperty("data").GetProperty("deleteComment").GetBoolean());
    }

    [Fact]
    public async Task AC_graphql_06_follow_mutation_routes_to_friends()
    {
        _factory.Friends.On("PUT", "/api/users/bob/follow", new StubReply(204));
        _factory.Friends.On("GET", "/api/friends/bob/stats", new StubReply(200, new { followersCount = 1, followingCount = 0, isFollowing = true, followsYou = false, isMutual = false }));
        using var client = _factory.ClientFor("alice");

        var result = await GraphQLFactory.ExecuteAsync(client, "mutation { follow(userId: \"bob\") { followersCount isFollowing } }", Ct);

        Assert.True(result.GetProperty("data").GetProperty("follow").GetProperty("isFollowing").GetBoolean());
        Assert.Equal(1, _factory.Friends.CountOf("PUT", "/api/users/bob/follow"));
    }

    [Fact]
    public async Task AC_graphql_07_downstream_404_becomes_a_NOT_FOUND_error()
    {
        using var client = _factory.ClientFor("alice");

        var result = await GraphQLFactory.ExecuteAsync(client, "{ user(id: \"ghost\") { id } }", Ct);

        var error = result.GetProperty("errors")[0];
        Assert.Equal("NOT_FOUND", error.GetProperty("extensions").GetProperty("code").GetString());
        Assert.DoesNotContain("ghost", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC_graphql_08_downstream_failures_map_to_validation_and_unavailable()
    {
        _factory.Feed.On("GET", "/api/feed", new StubReply(400, new { title = "bad" }));
        _factory.Friends.On("GET", "/api/friends/recommendations", new StubReply(503));
        using var client = _factory.ClientFor("alice");

        var bad = await GraphQLFactory.ExecuteAsync(client, "{ feed(limit: 30) { nextCursor } }", Ct);
        var down = await GraphQLFactory.ExecuteAsync(client, "{ recommendations { mutualFriends } }", Ct);

        Assert.Equal("VALIDATION", bad.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Equal("UNAVAILABLE", down.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC_graphql_09_page_size_above_the_limit_is_rejected_without_calling_services()
    {
        using var client = _factory.ClientFor("alice");

        var result = await GraphQLFactory.ExecuteAsync(client, "{ followers(userId: \"bob\", limit: 51) { total } }", Ct);

        Assert.Equal("VALIDATION", result.GetProperty("errors")[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Empty(_factory.Friends.Calls);
    }

    [Fact]
    public async Task AC_graphql_10_too_deep_queries_are_rejected_before_execution()
    {
        using var client = _factory.ClientFor("alice");
        const string deep = "{ feed { items { owner { videos { items { owner { videos { items { owner { id } } } } } } } } } }";

        var result = await GraphQLFactory.ExecuteAsync(client, deep, Ct);

        Assert.True(result.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0, result.ToString());
        Assert.All(_factory.All, s => Assert.Empty(s.Calls));
    }

    [Fact]
    public async Task AC_graphql_11_introspection_is_disabled_by_default()
    {
        using var client = _factory.ClientFor("alice");

        var result = await GraphQLFactory.ExecuteAsync(client, "{ __schema { types { name } } }", Ct);

        Assert.True(result.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0, result.ToString());
    }

    [Fact]
    public async Task AC_graphql_12_graphql_path_is_not_proxied_to_a_service()
    {
        using var client = _factory.ClientFor("alice");

        using var response = await client.GetAsync("/graphql", Ct);

        Assert.All(_factory.All, s => Assert.Empty(s.Calls));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}

public sealed class GraphQLIntrospectionTests : IClassFixture<IntrospectionFactory>
{
    private readonly IntrospectionFactory _factory;

    public GraphQLIntrospectionTests(IntrospectionFactory factory) => _factory = factory;

    [Fact]
    public async Task AC_graphql_13_introspection_works_when_enabled_by_configuration()
    {
        using var client = _factory.ClientFor("alice");

        var result = await GraphQLFactory.ExecuteAsync(client, "{ __schema { queryType { name } mutationType { name } } }", TestContext.Current.CancellationToken);

        Assert.False(result.TryGetProperty("errors", out _), result.ToString());
        Assert.Equal("Query", result.GetProperty("data").GetProperty("__schema").GetProperty("queryType").GetProperty("name").GetString());
        Assert.Equal("Mutation", result.GetProperty("data").GetProperty("__schema").GetProperty("mutationType").GetProperty("name").GetString());
    }
}
