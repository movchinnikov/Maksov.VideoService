using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maksov.VideoService.TestSupport;
using Maksov.VideoService.Users.Bll.Ports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maksov.VideoService.Users.Tests;

public sealed class UsersFactory : WebApplicationFactory<Program>
{
    public const string InternalKey = "test-internal-key";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=unused;Database=unused");
        builder.UseSetting("Internal:ApiKey", InternalKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProfileRepository>();
            services.AddSingleton<IProfileRepository, InMemoryProfiles>();
            services.AddTestAuthentication();
        });
    }

    public HttpClient ClientFor(string userId) => CreateClient().ClientFor(userId);
}

public sealed class InMemoryProfiles : IProfileRepository
{
    private readonly ConcurrentDictionary<string, ProfileRecord> _items = new();

    public Task<ProfileRecord> GetOrCreateAsync(string userId, string username, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(_items.AddOrUpdate(
            userId,
            _ => new ProfileRecord(userId, username, username, "", now, now),
            (_, existing) => existing with { Username = username }));
    }

    public Task<ProfileRecord?> GetAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(_items.TryGetValue(userId, out var p) ? p : null);

    public Task<ProfileRecord?> UpdateAsync(string userId, string displayName, string bio, CancellationToken cancellationToken)
    {
        if (!_items.TryGetValue(userId, out var p))
        {
            return Task.FromResult<ProfileRecord?>(null);
        }

        var updated = p with { DisplayName = displayName, Bio = bio, UpdatedAt = DateTimeOffset.UtcNow };
        _items[userId] = updated;
        return Task.FromResult<ProfileRecord?>(updated);
    }

    public Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken) =>
        Task.FromResult(_items.ContainsKey(userId));

    public Task<IReadOnlyList<ProfileSummary>> GetManyAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfileSummary> found = _items.Values
            .Where(p => userIds.Contains(p.UserId))
            .OrderBy(p => p.Username, StringComparer.Ordinal)
            .Select(p => new ProfileSummary(p.UserId, p.Username, p.DisplayName, p.Bio))
            .ToList();
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<ProfileSummary>> SearchAsync(string query, string viewerId, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProfileSummary> found = _items.Values
            .Where(p => p.UserId != viewerId
                && (p.Username.StartsWith(query, StringComparison.OrdinalIgnoreCase) || p.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Username, StringComparer.Ordinal)
            .Take(limit)
            .Select(p => new ProfileSummary(p.UserId, p.Username, p.DisplayName, p.Bio))
            .ToList();
        return Task.FromResult(found);
    }
}

public sealed class UsersApiTests : IClassFixture<UsersFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly UsersFactory _factory;

    public UsersApiTests(UsersFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC_users_01_anonymous_is_unauthorized()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/users/me", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC_users_02_first_request_creates_profile_with_username_as_display_name()
    {
        using var client = _factory.ClientFor("alice");

        var me = await client.GetFromJsonAsync<JsonElement>("/api/users/me", Json, Ct);

        Assert.Equal("alice", me.GetProperty("id").GetString());
        Assert.Equal("alice", me.GetProperty("username").GetString());
        Assert.Equal("alice", me.GetProperty("displayName").GetString());
        Assert.Equal("", me.GetProperty("bio").GetString());
    }

    [Fact]
    public async Task AC_users_03_update_trims_and_persists()
    {
        using var client = _factory.ClientFor("bob");

        using var put = await client.PutAsJsonAsync("/api/users/me", new { displayName = "  Bob Editor  ", bio = " shooting vertical " }, Ct);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/users/me", Json, Ct);

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Bob Editor", me.GetProperty("displayName").GetString());
        Assert.Equal("shooting vertical", me.GetProperty("bio").GetString());
    }

    [Theory]
    [InlineData("", "ok", "displayName")]
    [InlineData("   ", "ok", "displayName")]
    public async Task AC_users_04_blank_name_is_rejected(string displayName, string bio, string field)
    {
        using var client = _factory.ClientFor("carol");

        using var response = await client.PutAsJsonAsync("/api/users/me", new { displayName, bio }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task AC_users_04_too_long_fields_are_rejected()
    {
        using var client = _factory.ClientFor("dave");

        using var response = await client.PutAsJsonAsync("/api/users/me", new { displayName = new string('x', 61), bio = new string('y', 301) }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("displayName", out _));
        Assert.True(errors.TryGetProperty("bio", out _));
    }

    [Fact]
    public async Task AC_users_05_public_profile_and_unknown_is_404()
    {
        using var erin = _factory.ClientFor("erin");
        using var frank = _factory.ClientFor("frank");
        await erin.GetAsync("/api/users/me", Ct);
        await erin.PutAsJsonAsync("/api/users/me", new { displayName = "Erin", bio = "about me" }, Ct);

        var profile = await frank.GetFromJsonAsync<JsonElement>("/api/users/erin", Json, Ct);
        using var missing = await frank.GetAsync("/api/users/nobody", Ct);

        Assert.Equal("Erin", profile.GetProperty("displayName").GetString());
        Assert.Equal("erin", profile.GetProperty("username").GetString());
        Assert.False(profile.TryGetProperty("isFollowing", out _));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task AC_users_06_cannot_update_someone_elses_profile_through_body()
    {
        using var grace = _factory.ClientFor("grace");
        using var heidi = _factory.ClientFor("heidi");
        await grace.GetAsync("/api/users/me", Ct);

        await heidi.PutAsJsonAsync("/api/users/me", new { id = "grace", displayName = "Hacked", bio = "" }, Ct);
        var graceProfile = await grace.GetFromJsonAsync<JsonElement>("/api/users/me", Json, Ct);

        Assert.Equal("grace", graceProfile.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task AC_users_07_profiles_carry_no_follow_data()
    {
        using var alice = _factory.ClientFor("pa-" + Guid.NewGuid().ToString("N"));

        var me = await alice.GetFromJsonAsync<JsonElement>("/api/users/me", Json, Ct);

        Assert.False(me.TryGetProperty("followersCount", out _));
        Assert.False(me.TryGetProperty("followingCount", out _));
    }

    [Fact]
    public async Task AC_users_08_follow_routes_are_not_served_by_users()
    {
        using var client = _factory.ClientFor("loner-" + Guid.NewGuid().ToString("N"));

        using var put = await client.PutAsync("/api/users/someone/follow", null, Ct);

        Assert.True(put.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task AC_users_09_search_finds_by_prefix_excludes_self_and_empty_query_is_empty()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        using var seeker = _factory.ClientFor($"seeker{tag}");
        using var target = _factory.ClientFor($"zed{tag}");
        await seeker.GetAsync("/api/users/me", Ct);
        await target.GetAsync("/api/users/me", Ct);

        var found = await seeker.GetFromJsonAsync<JsonElement>($"/api/users?query=ZED{tag}", Json, Ct);
        var self = await seeker.GetFromJsonAsync<JsonElement>($"/api/users?query=seeker{tag}", Json, Ct);
        var empty = await seeker.GetFromJsonAsync<JsonElement>("/api/users?query=", Json, Ct);

        var item = Assert.Single(found.GetProperty("items").EnumerateArray());
        Assert.Equal($"zed{tag}", item.GetProperty("username").GetString());
        Assert.Empty(self.GetProperty("items").EnumerateArray());
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task AC_users_10_batch_lookup_by_ids()
    {
        var a = "ba-" + Guid.NewGuid().ToString("N");
        var b = "bb-" + Guid.NewGuid().ToString("N");
        using var alice = _factory.ClientFor(a);
        using var bob = _factory.ClientFor(b);
        await alice.GetAsync("/api/users/me", Ct);
        await bob.GetAsync("/api/users/me", Ct);

        var found = await alice.GetFromJsonAsync<JsonElement>($"/api/users?ids={a},{b},missing", Json, Ct);

        Assert.Equal(new[] { a, b }.Order(StringComparer.Ordinal), found.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AC_users_11_internal_exists_requires_the_key()
    {
        var a = "ea-" + Guid.NewGuid().ToString("N");
        using var alice = _factory.ClientFor(a);
        await alice.GetAsync("/api/users/me", Ct);
        using var anonymous = _factory.CreateClient();

        HttpRequestMessage Request(string id, string? key)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/users/{id}/exists");
            if (key is not null)
            {
                request.Headers.Add("X-Internal-Key", key);
            }
            return request;
        }

        using var noKey = await anonymous.SendAsync(Request(a, null), Ct);
        using var exists = await anonymous.SendAsync(Request(a, UsersFactory.InternalKey), Ct);
        using var missing = await anonymous.SendAsync(Request("nobody-" + Guid.NewGuid().ToString("N"), UsersFactory.InternalKey), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, exists.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
