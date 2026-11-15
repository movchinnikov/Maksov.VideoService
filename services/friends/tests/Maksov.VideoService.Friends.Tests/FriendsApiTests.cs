using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maksov.VideoService.Friends.Bll;
using Maksov.VideoService.Friends.Bll.Ports;
using Maksov.VideoService.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maksov.VideoService.Friends.Tests;

public sealed class FriendsFactory : WebApplicationFactory<Program>
{
    public const string InternalKey = "test-internal-key";

    public InMemoryFriends Friends { get; } = new();
    public FakeProfiles Profiles { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=unused;Database=unused");
        builder.UseSetting("Internal:ApiKey", InternalKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IFriendsRepository>();
            services.AddSingleton<IFriendsRepository>(Friends);
            services.RemoveAll<IProfileDirectory>();
            services.AddSingleton<IProfileDirectory>(Profiles);
            services.AddTestAuthentication();
        });
    }

    public HttpClient ClientFor(string userId) => CreateClient().ClientFor(userId);
}

public sealed class FakeProfiles : IProfileDirectory
{
    private readonly ConcurrentDictionary<string, byte> _known = new();

    public bool Unavailable { get; set; }

    public void Add(params string[] ids)
    {
        foreach (var id in ids)
        {
            _known[id] = 0;
        }
    }

    public Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken) =>
        Unavailable ? throw new HttpRequestException("users down") : Task.FromResult(_known.ContainsKey(userId));
}

public sealed class InMemoryFriends : IFriendsRepository
{
    private readonly ConcurrentDictionary<(string Follower, string Followee), DateTimeOffset> _follows = new();

    public Task FollowAsync(string followerId, string followeeId, CancellationToken cancellationToken)
    {
        _follows.TryAdd((followerId, followeeId), DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task UnfollowAsync(string followerId, string followeeId, CancellationToken cancellationToken)
    {
        _follows.TryRemove((followerId, followeeId), out _);
        return Task.CompletedTask;
    }

    public Task<FriendStats> GetStatsAsync(string userId, string viewerId, CancellationToken cancellationToken) =>
        Task.FromResult(new FriendStats(
            _follows.Keys.Count(k => k.Followee == userId),
            _follows.Keys.Count(k => k.Follower == userId),
            _follows.ContainsKey((viewerId, userId)),
            _follows.ContainsKey((userId, viewerId))));

    public Task<IReadOnlyList<string>> GetFollowedAmongAsync(string viewerId, IReadOnlyCollection<string> candidateIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> found = candidateIds.Where(id => _follows.ContainsKey((viewerId, id))).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<string>> GetFollowedByAmongAsync(string viewerId, IReadOnlyCollection<string> candidateIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> found = candidateIds.Where(id => _follows.ContainsKey((id, viewerId))).ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<RelationRow>> ListFollowersAsync(string userId, string viewerId, RelationCursor? after, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RelationRow>>(List(_follows.Where(k => k.Key.Followee == userId).Select(k => (Other: k.Key.Follower, At: k.Value)), viewerId, after, limit));

    public Task<IReadOnlyList<RelationRow>> ListFollowingAsync(string userId, string viewerId, RelationCursor? after, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RelationRow>>(List(_follows.Where(k => k.Key.Follower == userId).Select(k => (Other: k.Key.Followee, At: k.Value)), viewerId, after, limit));

    private List<RelationRow> List(IEnumerable<(string Other, DateTimeOffset At)> rows, string viewerId, RelationCursor? after, int limit) =>
        rows
            .Where(r => after is null || r.At < after.FollowedAt || (r.At == after.FollowedAt && string.CompareOrdinal(r.Other, after.UserId) < 0))
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Other, StringComparer.Ordinal)
            .Take(limit)
            .Select(r => new RelationRow(r.Other, r.At, _follows.ContainsKey((viewerId, r.Other)), _follows.ContainsKey((r.Other, viewerId))))
            .ToList();

    public Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> ids = _follows.Where(k => k.Key.Follower == userId).OrderByDescending(k => k.Value).Select(k => k.Key.Followee).Take(limit).ToList();
        return Task.FromResult(ids);
    }

    public Task<IReadOnlyList<Recommendation>> RecommendAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        var mine = _follows.Keys.Where(k => k.Follower == userId).Select(k => k.Followee).ToHashSet();
        var mineEmbedding = GraphEmbedding.Compute(userId, mine);
        IReadOnlyList<Recommendation> rows = _follows.Keys
            .Where(k => mine.Contains(k.Follower) && k.Followee != userId && !mine.Contains(k.Followee))
            .GroupBy(k => k.Followee)
            .Select(g => new Recommendation(g.Key, g.Count(), GraphEmbedding.Cosine(mineEmbedding, GraphEmbedding.Compute(g.Key, _follows.Keys.Where(k => k.Follower == g.Key).Select(k => k.Followee)))))
            .OrderByDescending(r => r.Similarity).ThenByDescending(r => r.MutualFriends)
            .Take(limit)
            .ToList();
        return Task.FromResult(rows);
    }
}

public sealed class FriendsApiTests(FriendsFactory factory) : IClassFixture<FriendsFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Id(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task AC_friends_01_requires_authentication()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/friends/someone/stats", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC_friends_02_follow_updates_stats_and_is_idempotent()
    {
        var a = Id("fa");
        var b = Id("fb");
        factory.Profiles.Add(a, b);
        using var alice = factory.ClientFor(a);
        using var bob = factory.ClientFor(b);

        using var first = await alice.PutAsync($"/api/users/{b}/follow", null, Ct);
        using var second = await alice.PutAsync($"/api/users/{b}/follow", null, Ct);
        var bobSeenByAlice = await alice.GetFromJsonAsync<JsonElement>($"/api/friends/{b}/stats", Json, Ct);
        var aliceSeenByAlice = await alice.GetFromJsonAsync<JsonElement>($"/api/friends/{a}/stats", Json, Ct);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.True(bobSeenByAlice.GetProperty("isFollowing").GetBoolean());
        Assert.Equal(1, bobSeenByAlice.GetProperty("followersCount").GetInt32());
        Assert.Equal(1, aliceSeenByAlice.GetProperty("followingCount").GetInt32());

        using var unfollow = await alice.DeleteAsync($"/api/users/{b}/follow", Ct);
        using var unfollowAgain = await alice.DeleteAsync($"/api/users/{b}/follow", Ct);
        var after = await alice.GetFromJsonAsync<JsonElement>($"/api/friends/{b}/stats", Json, Ct);

        Assert.Equal(HttpStatusCode.NoContent, unfollow.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unfollowAgain.StatusCode);
        Assert.False(after.GetProperty("isFollowing").GetBoolean());
        Assert.Equal(0, after.GetProperty("followersCount").GetInt32());
    }

    [Fact]
    public async Task AC_friends_03_cannot_follow_self_or_unknown_profile()
    {
        var a = Id("self");
        factory.Profiles.Add(a);
        using var client = factory.ClientFor(a);

        using var self = await client.PutAsync($"/api/users/{a}/follow", null, Ct);
        using var unknown = await client.PutAsync("/api/users/nobody-at-all/follow", null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AC_friends_04_follow_is_503_when_users_service_is_down()
    {
        using var client = factory.ClientFor(Id("down"));
        factory.Profiles.Unavailable = true;
        try
        {
            using var response = await client.PutAsync("/api/users/anyone/follow", null, Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            factory.Profiles.Unavailable = false;
        }
    }

    [Fact]
    public async Task AC_friends_05_status_tells_which_ids_are_followed()
    {
        var a = Id("sa");
        var b = Id("sb");
        var c = Id("sc");
        factory.Profiles.Add(a, b, c);
        using var alice = factory.ClientFor(a);
        await alice.PutAsync($"/api/users/{b}/follow", null, Ct);

        var status = await alice.GetFromJsonAsync<JsonElement>($"/api/friends/status?ids={b},{c}", Json, Ct);

        Assert.Equal([b], status.GetProperty("following").EnumerateArray().Select(i => i.GetString()));
        Assert.Empty(status.GetProperty("followedBy").EnumerateArray());
    }

    [Fact]
    public async Task AC_friends_06_recommendations_are_friends_of_friends_not_yet_followed()
    {
        var me = Id("me");
        var friend = Id("friend");
        var fof = Id("fof");
        var already = Id("already");
        factory.Profiles.Add(me, friend, fof, already);
        using var meClient = factory.ClientFor(me);
        using var friendClient = factory.ClientFor(friend);
        await meClient.PutAsync($"/api/users/{friend}/follow", null, Ct);
        await meClient.PutAsync($"/api/users/{already}/follow", null, Ct);
        await friendClient.PutAsync($"/api/users/{fof}/follow", null, Ct);
        await friendClient.PutAsync($"/api/users/{already}/follow", null, Ct);
        await friendClient.PutAsync($"/api/users/{me}/follow", null, Ct);

        var recommendations = await meClient.GetFromJsonAsync<JsonElement>("/api/friends/recommendations", Json, Ct);

        var item = Assert.Single(recommendations.GetProperty("items").EnumerateArray());
        Assert.Equal(fof, item.GetProperty("userId").GetString());
        Assert.Equal(1, item.GetProperty("mutualFriends").GetInt32());
    }

    [Fact]
    public async Task AC_friends_07_internal_following_ids_require_the_key()
    {
        var a = Id("ia");
        var b = Id("ib");
        factory.Profiles.Add(a, b);
        using var alice = factory.ClientFor(a);
        await alice.PutAsync($"/api/users/{b}/follow", null, Ct);

        using var anonymous = factory.CreateClient();
        using var noKey = await anonymous.GetAsync($"/internal/v1/friends/{a}/following-ids", Ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/friends/{a}/following-ids");
        request.Headers.Add("X-Internal-Key", FriendsFactory.InternalKey);
        using var withKey = await anonymous.SendAsync(request, Ct);
        var body = await withKey.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        Assert.Equal([b], body.GetProperty("ids").EnumerateArray().Select(i => i.GetString()));
    }

    [Fact]
    public async Task AC_friends_08_stats_and_status_expose_followed_by_and_mutual()
    {
        var a = Id("ma");
        var b = Id("mb");
        factory.Profiles.Add(a, b);
        using var alice = factory.ClientFor(a);
        using var bob = factory.ClientFor(b);

        await bob.PutAsync($"/api/users/{a}/follow", null, Ct);
        var oneWay = await alice.GetFromJsonAsync<JsonElement>($"/api/friends/{b}/stats", Json, Ct);
        var statusOneWay = await alice.GetFromJsonAsync<JsonElement>($"/api/friends/status?ids={b}", Json, Ct);
        await alice.PutAsync($"/api/users/{b}/follow", null, Ct);
        var mutual = await alice.GetFromJsonAsync<JsonElement>($"/api/friends/{b}/stats", Json, Ct);

        Assert.True(oneWay.GetProperty("followsYou").GetBoolean());
        Assert.False(oneWay.GetProperty("isFollowing").GetBoolean());
        Assert.False(oneWay.GetProperty("isMutual").GetBoolean());
        Assert.Equal([b], statusOneWay.GetProperty("followedBy").EnumerateArray().Select(i => i.GetString()));
        Assert.True(mutual.GetProperty("isMutual").GetBoolean());
    }

    [Fact]
    public async Task AC_friends_09_followers_and_following_lists_are_paged_with_relation_flags()
    {
        var me = Id("lm");
        var f1 = Id("l1");
        var f2 = Id("l2");
        var f3 = Id("l3");
        factory.Profiles.Add(me, f1, f2, f3);
        using var meClient = factory.ClientFor(me);
        foreach (var f in new[] { f1, f2, f3 })
        {
            using var fc = factory.ClientFor(f);
            await fc.PutAsync($"/api/users/{me}/follow", null, Ct);
            await Task.Delay(5, Ct);
        }

        await meClient.PutAsync($"/api/users/{f2}/follow", null, Ct); // f2 is mutual, f1 and f3 only follow me

        var page1 = await meClient.GetFromJsonAsync<JsonElement>($"/api/friends/{me}/followers?limit=2", Json, Ct);
        var cursor = page1.GetProperty("nextCursor").GetString();
        var page2 = await meClient.GetFromJsonAsync<JsonElement>($"/api/friends/{me}/followers?limit=2&cursor={cursor}", Json, Ct);
        var following = await meClient.GetFromJsonAsync<JsonElement>($"/api/friends/{me}/following", Json, Ct);

        var items1 = page1.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([f3, f2], items1.Select(i => i.GetProperty("userId").GetString()));
        Assert.Equal(3, page1.GetProperty("total").GetInt32());
        Assert.True(items1[1].GetProperty("isMutual").GetBoolean());
        Assert.True(items1[0].GetProperty("followsYou").GetBoolean());
        Assert.False(items1[0].GetProperty("isMutual").GetBoolean());
        Assert.Equal([f1], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetString()));
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
        var followed = Assert.Single(following.GetProperty("items").EnumerateArray());
        Assert.Equal(f2, followed.GetProperty("userId").GetString());
        Assert.True(followed.GetProperty("isMutual").GetBoolean());
    }

    [Fact]
    public async Task AC_friends_10_lists_require_authentication_and_ignore_a_broken_cursor()
    {
        using var anonymous = factory.CreateClient();
        using var viewer = factory.ClientFor(Id("bc"));

        using var denied = await anonymous.GetAsync("/api/friends/x/followers", Ct);
        using var broken = await viewer.GetAsync("/api/friends/x/followers?cursor=%%%not-base64", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, broken.StatusCode);
    }
}
