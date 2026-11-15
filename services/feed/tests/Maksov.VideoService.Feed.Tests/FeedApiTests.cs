using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Maksov.VideoService.Feed.Bll.Ports;
using Maksov.VideoService.Feed.Dal.Http;
using Maksov.VideoService.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Feed.Tests;

public sealed class FakeFollowing : IFollowingSource
{
    public Dictionary<string, string[]> Map { get; } = [];

    public bool Unavailable { get; set; }

    public Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, CancellationToken cancellationToken) =>
        Unavailable
            ? throw new HttpRequestException("friends down")
            : Task.FromResult<IReadOnlyList<string>>(Map.TryGetValue(userId, out var ids) ? ids : []);
}

public sealed class FakeCatalog : IVideoCatalog
{
    public bool Unavailable { get; set; }

    public List<(string[] Owners, string? Cursor, int Limit)> Calls { get; } = [];

    public Task<VideoIdPage> ReadyByOwnersAsync(IReadOnlyCollection<string> ownerIds, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new HttpRequestException("videos down");
        }

        Calls.Add((ownerIds.ToArray(), cursor, limit));
        return Task.FromResult(new VideoIdPage([Guid.Parse("00000000-0000-0000-0000-000000000001")], "next-cursor"));
    }
}

public sealed class FeedFactory : WebApplicationFactory<Program>
{
    public FakeFollowing Following { get; } = new();
    public FakeCatalog Catalog { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFollowingSource>();
            services.AddSingleton<IFollowingSource>(Following);
            services.RemoveAll<IVideoCatalog>();
            services.AddSingleton<IVideoCatalog>(Catalog);
            services.AddTestAuthentication();
        });

    public HttpClient ClientFor(string userId) => CreateClient().ClientFor(userId);
}

public sealed class FeedApiTests(FeedFactory factory) : IClassFixture<FeedFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC_feed_01_requires_authentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/feed", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC_feed_02_asks_videos_for_followed_owners_and_the_viewer_and_returns_ids_only()
    {
        factory.Following.Map["me"] = ["friend-1", "friend-2"];
        factory.Catalog.Calls.Clear();
        using var client = factory.ClientFor("me");

        var page = await client.GetFromJsonAsync<JsonElement>("/api/feed?cursor=abc&limit=5", Json, Ct);

        var call = Assert.Single(factory.Catalog.Calls);
        Assert.Equal(["friend-1", "friend-2", "me"], call.Owners.Order(StringComparer.Ordinal));
        Assert.Equal(("abc", 5), (call.Cursor, call.Limit));
        Assert.Equal(["00000000-0000-0000-0000-000000000001"], page.GetProperty("ids").EnumerateArray().Select(i => i.GetString()));
        Assert.Equal("next-cursor", page.GetProperty("nextCursor").GetString());
        Assert.False(page.TryGetProperty("items", out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1000, 30)]
    public async Task AC_feed_03_limit_is_clamped(int requested, int expected)
    {
        factory.Catalog.Calls.Clear();
        using var client = factory.ClientFor("clamp");

        using var response = await client.GetAsync($"/api/feed?limit={requested}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, Assert.Single(factory.Catalog.Calls).Limit);
    }

    [Fact]
    public async Task AC_feed_04_degrades_to_own_videos_when_friends_is_down()
    {
        factory.Catalog.Calls.Clear();
        factory.Following.Unavailable = true;
        try
        {
            using var client = factory.ClientFor("lonely");

            using var response = await client.GetAsync("/api/feed", Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["lonely"], Assert.Single(factory.Catalog.Calls).Owners);
        }
        finally
        {
            factory.Following.Unavailable = false;
        }
    }

    [Fact]
    public async Task AC_feed_05_is_503_when_videos_is_down()
    {
        factory.Catalog.Unavailable = true;
        try
        {
            using var client = factory.ClientFor("any");

            using var response = await client.GetAsync("/api/feed", Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            factory.Catalog.Unavailable = false;
        }
    }
}

public sealed class FriendsFollowingSourceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Following_ids_are_cached_and_the_key_is_sent()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ids\":[\"a\",\"b\"]}", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://friends.test") };
        using var cache = new MemoryCache(Options.Create(new MemoryCacheOptions { SizeLimit = 100 }));
        var source = new FriendsFollowingSource(http, cache, "secret-key");

        var first = await source.GetFollowingIdsAsync("u1", Ct);
        var second = await source.GetFollowingIdsAsync("u1", Ct);

        Assert.Equal(["a", "b"], first);
        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("secret-key", handler.Last!.Headers.GetValues("X-Internal-Key").Single());
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        var fail = true;
        var handler = new StubHandler(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ids\":[\"x\"]}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://friends.test") };
        using var cache = new MemoryCache(Options.Create(new MemoryCacheOptions { SizeLimit = 100 }));
        var source = new FriendsFollowingSource(http, cache, null);

        await Assert.ThrowsAsync<HttpRequestException>(() => source.GetFollowingIdsAsync("u2", Ct));
        fail = false;

        Assert.Equal(["x"], await source.GetFollowingIdsAsync("u2", Ct));
    }
}
