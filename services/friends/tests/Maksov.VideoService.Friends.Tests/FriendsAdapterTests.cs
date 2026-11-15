using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.Friends.Bll.Ports;
using Maksov.VideoService.Friends.Dal;
using Maksov.VideoService.Friends.Dal.Postgres;
using Maksov.VideoService.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Maksov.VideoService.Friends.Tests;

/// <summary>Postgres + pgvector adapter on a real database (needs the pgvector image).</summary>
public sealed class FriendsAdapterTests : IAsyncLifetime
{
    private NpgsqlDataSource? _db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabases.CreateAsync("friends_adapter", Ct);
        if (_db is null)
        {
            return;
        }

        await new SqlMigrator(_db, NullLogger<SqlMigrator>.Instance).MigrateAsync(DalServiceCollectionExtensions.MigrationsAssembly, Ct);
        await using var cmd = _db.CreateCommand("truncate follows, user_embeddings");
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgresFriendsRepository Repo
    {
        get
        {
            if (_db is null)
            {
                Assert.Skip("VIDEOSERVICE_TEST_POSTGRES is not set");
            }
            return new PostgresFriendsRepository(_db!);
        }
    }

    [Fact]
    public async Task Follow_is_idempotent_counts_and_unfollow_works()
    {
        await Repo.FollowAsync("a", "b", Ct);
        await Repo.FollowAsync("a", "b", Ct);

        var b = await Repo.GetStatsAsync("b", "a", Ct);
        var a = await Repo.GetStatsAsync("a", "a", Ct);
        Assert.Equal((1, 0, true, false), (b.FollowersCount, b.FollowingCount, b.IsFollowing, b.FollowsYou));
        Assert.Equal((0, 1), (a.FollowersCount, a.FollowingCount));
        Assert.Equal(["b"], await Repo.GetFollowingIdsAsync("a", 10, Ct));
        Assert.Equal(["b"], await Repo.GetFollowedAmongAsync("a", ["b", "c"], Ct));

        await Repo.UnfollowAsync("a", "b", Ct);
        await Repo.UnfollowAsync("a", "b", Ct);
        Assert.Equal(0, (await Repo.GetStatsAsync("b", "a", Ct)).FollowersCount);
    }

    [Fact]
    public async Task Self_follow_is_blocked_by_the_database()
    {
        await Assert.ThrowsAsync<PostgresException>(() => Repo.FollowAsync("loner", "loner", Ct));
    }

    [Fact]
    public async Task Embeddings_are_stored_with_model_version_in_a_vector_column()
    {
        await Repo.FollowAsync("a", "b", Ct);

        await using var cmd = _db!.CreateCommand("select count(*)::int, min(model_version), min(vector_dims(embedding)) from user_embeddings");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(2, reader.GetInt32(0)); // follower and followee
        Assert.Equal("graph-hash-v1", reader.GetString(1));
        Assert.Equal(32, reader.GetInt32(2));
    }

    [Fact]
    public async Task Recommendations_are_friends_of_friends_ranked_by_similarity()
    {
        // me -> f1, f2 ; f1 -> x, y ; f2 -> x ; me already follows f1, f2 ; x is followed by both friends, y by one
        await Repo.FollowAsync("me", "f1", Ct);
        await Repo.FollowAsync("me", "f2", Ct);
        await Repo.FollowAsync("f1", "x", Ct);
        await Repo.FollowAsync("f1", "y", Ct);
        await Repo.FollowAsync("f2", "x", Ct);

        var recommendations = await Repo.RecommendAsync("me", 10, Ct);

        Assert.Equal(["x", "y"], recommendations.Select(r => r.UserId).Order(StringComparer.Ordinal));
        Assert.Equal(2, recommendations.Single(r => r.UserId == "x").MutualFriends);
        Assert.Equal(1, recommendations.Single(r => r.UserId == "y").MutualFriends);
        Assert.All(recommendations, r => Assert.InRange(r.Similarity, -1.0001, 1.0001));
        Assert.DoesNotContain(recommendations, r => r.UserId is "me" or "f1" or "f2");
        Assert.Empty(await Repo.RecommendAsync("nobody", 10, Ct));
    }

    [Fact]
    public async Task Follower_and_following_lists_page_by_keyset_and_flag_the_relation_to_the_viewer()
    {
        // u1, u2, u3 follow "me" in this order; "me" follows u2 back (mutual) and u9.
        await Repo.FollowAsync("u1", "me", Ct);
        await Task.Delay(5, Ct);
        await Repo.FollowAsync("u2", "me", Ct);
        await Task.Delay(5, Ct);
        await Repo.FollowAsync("u3", "me", Ct);
        await Repo.FollowAsync("me", "u2", Ct);
        await Repo.FollowAsync("me", "u9", Ct);

        var page1 = await Repo.ListFollowersAsync("me", "me", null, 2, Ct);
        var page2 = await Repo.ListFollowersAsync("me", "me", new RelationCursor(page1[^1].FollowedAt, page1[^1].UserId), 2, Ct);
        var following = await Repo.ListFollowingAsync("me", "me", null, 10, Ct);
        var asStranger = await Repo.ListFollowersAsync("me", "u1", null, 10, Ct);

        Assert.Equal(["u3", "u2"], page1.Select(r => r.UserId));
        Assert.Equal(["u1"], page2.Select(r => r.UserId));
        Assert.Equal((true, true), (page1[1].IsFollowing, page1[1].FollowsYou)); // u2 is mutual
        Assert.Equal((false, true), (page1[0].IsFollowing, page1[0].FollowsYou)); // u3 only follows me
        Assert.Equal(["u9", "u2"], following.Select(r => r.UserId));
        Assert.Equal((true, false), (following[0].IsFollowing, following[0].FollowsYou)); // u9 does not follow back
        Assert.Equal(["u3", "u2", "u1"], asStranger.Select(r => r.UserId));
        Assert.False(asStranger.Single(r => r.UserId == "u1").IsFollowing); // the viewer u1 is not in his own row relations
        Assert.Equal(["u2"], await Repo.GetFollowedByAmongAsync("me", ["u2", "u9", "zzz"], Ct));
    }

    [Fact]
    public void Relation_cursor_round_trips_and_rejects_garbage()
    {
        var cursor = new RelationCursor(DateTimeOffset.UtcNow, "user:with:colons");

        var decoded = RelationCursor.TryDecode(cursor.Encode());

        Assert.Equal(cursor, decoded);
        Assert.Null(RelationCursor.TryDecode("%%%"));
        Assert.Null(RelationCursor.TryDecode(null));
    }
}
