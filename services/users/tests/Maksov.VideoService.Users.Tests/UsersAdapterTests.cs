using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.TestSupport;
using Maksov.VideoService.Users.Dal.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using UsersDal = Maksov.VideoService.Users.Dal.DalServiceCollectionExtensions;

namespace Maksov.VideoService.Users.Tests;

/// <summary>Postgres adapter of the users service on a real DB.</summary>
public sealed class UsersAdapterTests : IAsyncLifetime
{
    private NpgsqlDataSource? _db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabases.CreateAsync("users", Ct);
        if (_db is null)
        {
            return;
        }

        await new SqlMigrator(_db, NullLogger<SqlMigrator>.Instance).MigrateAsync(UsersDal.MigrationsAssembly, Ct);
        await using var cmd = _db.CreateCommand("truncate profiles");
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgresProfileRepository Repo
    {
        get
        {
            if (_db is null)
            {
                Assert.Skip("VIDEOSERVICE_TEST_POSTGRES is not set");
            }
            return new PostgresProfileRepository(_db!);
        }
    }

    [Fact]
    public async Task First_access_creates_profile_and_repeat_does_not_duplicate()
    {
        var first = await Repo.GetOrCreateAsync("u-1", "alice", Ct);
        var second = await Repo.GetOrCreateAsync("u-1", "alice-renamed", Ct);

        Assert.Equal("alice", first.DisplayName);
        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal("alice-renamed", second.Username);
        Assert.Equal("alice", second.DisplayName);
    }

    [Fact]
    public async Task Concurrent_first_requests_create_one_profile()
    {
        var repo = Repo;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repo.GetOrCreateAsync("u-race", "racer", Ct)));

        Assert.All(results, r => Assert.Equal("u-race", r.UserId));
        await using var cmd = _db!.CreateCommand("select count(*) from profiles where user_id = 'u-race'");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Update_changes_only_display_name_and_bio_and_unknown_user_is_null()
    {
        await Repo.GetOrCreateAsync("u-2", "bob", Ct);

        var updated = await Repo.UpdateAsync("u-2", "Bob", "editing", Ct);
        var missing = await Repo.UpdateAsync("nobody", "x", "y", Ct);

        Assert.Equal(("bob", "Bob", "editing"), (updated!.Username, updated.DisplayName, updated.Bio));
        Assert.Null(missing);
        Assert.Null(await Repo.GetAsync("nobody", Ct));
    }

    [Fact]
    public async Task Exists_and_batch_lookup()
    {
        await Repo.GetOrCreateAsync("f-a", "fa", Ct);
        await Repo.GetOrCreateAsync("f-b", "fb", Ct);

        Assert.True(await Repo.ExistsAsync("f-a", Ct));
        Assert.False(await Repo.ExistsAsync("ghost", Ct));
        var many = await Repo.GetManyAsync(["f-a", "f-b", "ghost"], Ct);
        Assert.Equal(["fa", "fb"], many.Select(p => p.Username));
        Assert.Empty(await Repo.GetManyAsync([], Ct));
    }

    [Fact]
    public async Task Search_matches_prefix_ignores_case_escapes_wildcards()
    {
        await Repo.GetOrCreateAsync("s-viewer", "viewer", Ct);
        await Repo.GetOrCreateAsync("s-1", "Anna_k", Ct);
        await Repo.GetOrCreateAsync("s-2", "annabel", Ct);
        await Repo.GetOrCreateAsync("s-3", "50%club", Ct);
        await Repo.UpdateAsync("s-3", "Club Fifty", "", Ct);

        var anna = await Repo.SearchAsync("ANNA", "s-viewer", 10, Ct);
        var percent = await Repo.SearchAsync("50%", "s-viewer", 10, Ct);
        var wildcard = await Repo.SearchAsync("%", "s-viewer", 10, Ct);
        var underscore = await Repo.SearchAsync("anna_", "s-viewer", 10, Ct);
        var byDisplayName = await Repo.SearchAsync("club", "s-viewer", 10, Ct);

        Assert.Equal(["Anna_k", "annabel"], anna.Select(p => p.Username).Order(StringComparer.Ordinal));
        Assert.Equal(["50%club"], percent.Select(p => p.Username));
        Assert.DoesNotContain(wildcard, p => p.UserId != "s-3");
        Assert.Equal(["Anna_k"], underscore.Select(p => p.Username));
        Assert.Equal(["50%club"], byDisplayName.Select(p => p.Username));
        Assert.DoesNotContain(anna, p => p.UserId == "s-viewer");
    }
}
