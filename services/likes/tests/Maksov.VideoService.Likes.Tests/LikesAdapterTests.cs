using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.Likes.Bll.Ports;
using Maksov.VideoService.Likes.Dal;
using Maksov.VideoService.Likes.Dal.Postgres;
using Maksov.VideoService.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Maksov.VideoService.Likes.Tests;

/// <summary>Postgres adapter: counters, idempotency with the outbox, cascade on deletion, tombstones, paging.</summary>
public sealed class LikesAdapterTests : IAsyncLifetime
{
    private NpgsqlDataSource? _db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabases.CreateAsync("likes_adapter", Ct);
        if (_db is null)
        {
            return;
        }

        await new SqlMigrator(_db, NullLogger<SqlMigrator>.Instance).MigrateAsync(DalServiceCollectionExtensions.MigrationsAssembly, Ct);
        await using var cmd = _db.CreateCommand("truncate known_videos, known_comments, likes, like_counts, outbox");
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgresLikeRepository Repo
    {
        get
        {
            if (_db is null)
            {
                Assert.Skip("VIDEOSERVICE_TEST_POSTGRES is not set");
            }

            return new PostgresLikeRepository(_db!);
        }
    }

    private async Task<LikeTarget> VideoAsync(string owner = "bob")
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertVideoAsync(id, owner, Ct);
        return new LikeTarget(LikeTargetType.Video, id, id, owner);
    }

    private async Task<LikeTarget> CommentAsync(Guid videoId, string author = "carol")
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertCommentAsync(id, videoId, author, Ct);
        return new LikeTarget(LikeTargetType.Comment, id, videoId, author);
    }

    private async Task<List<(string Subscriber, string Topic)>> OutboxAsync()
    {
        await using var cmd = _db!.CreateCommand("select subscriber, topic from outbox order by created_at, topic");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var cmd = _db!.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Video_registration_is_idempotent_and_owner_is_returned()
    {
        var id = Guid.CreateVersion7();

        await Repo.UpsertVideoAsync(id, "bob", Ct);
        await Repo.UpsertVideoAsync(id, "somebody-else", Ct);

        Assert.Equal("bob", await Repo.GetVideoOwnerAsync(id, Ct));
        Assert.Null(await Repo.GetVideoOwnerAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Like_is_idempotent_keeps_the_counter_consistent_and_emits_one_outbox_row_for_lake()
    {
        var video = await VideoAsync();

        Assert.True(await Repo.AddLikeAsync(video, "alice", Ct));
        Assert.False(await Repo.AddLikeAsync(video, "alice", Ct));
        Assert.True(await Repo.AddLikeAsync(video, "dave", Ct));

        var counts = await Repo.GetCountsAsync(LikeTargetType.Video, [video.Id], "alice", Ct);
        Assert.Equal(2, counts.Single().Count);
        Assert.True(counts.Single().LikedByMe);
        Assert.Equal(2, await ScalarAsync($"select count(*) from likes where target_id = '{video.Id}'"));
        Assert.Equal([("lake", "video.liked.v1"), ("lake", "video.liked.v1")], await OutboxAsync());
    }

    [Fact]
    public async Task Unlike_emits_unliked_once_and_decrements()
    {
        var video = await VideoAsync();
        await Repo.AddLikeAsync(video, "alice", Ct);

        Assert.True(await Repo.RemoveLikeAsync(video, "alice", Ct));
        Assert.False(await Repo.RemoveLikeAsync(video, "alice", Ct));
        Assert.False(await Repo.RemoveLikeAsync(video, "never-liked", Ct));

        var counts = await Repo.GetCountsAsync(LikeTargetType.Video, [video.Id], "alice", Ct);
        Assert.Equal(0, counts.Single().Count);
        Assert.False(counts.Single().LikedByMe);
        Assert.Equal([("lake", "video.liked.v1"), ("lake", "video.unliked.v1")], await OutboxAsync());
    }

    [Fact]
    public async Task Like_of_an_unknown_target_writes_nothing()
    {
        var ghost = new LikeTarget(LikeTargetType.Video, Guid.CreateVersion7(), Guid.CreateVersion7(), "bob");

        Assert.False(await Repo.AddLikeAsync(ghost, "alice", Ct));

        Assert.Equal(0, await ScalarAsync("select count(*) from likes"));
        Assert.Empty(await OutboxAsync());
    }

    [Fact]
    public async Task Comment_like_emits_comment_events_and_counts_separately_from_video()
    {
        var video = await VideoAsync();
        var comment = await CommentAsync(video.Id);

        Assert.True(await Repo.AddLikeAsync(comment, "alice", Ct));
        Assert.False(await Repo.AddLikeAsync(comment, "alice", Ct));
        Assert.True(await Repo.RemoveLikeAsync(comment, "alice", Ct));

        Assert.Equal(0, (await Repo.GetCountsAsync(LikeTargetType.Comment, [comment.Id], "alice", Ct)).Single().Count);
        Assert.Equal(0, await Repo.CountVideoLikesAsync(video.Id, Ct));
        Assert.Equal([("lake", "comment.liked.v1"), ("lake", "comment.unliked.v1")], await OutboxAsync());
        var known = await Repo.GetCommentAsync(comment.Id, Ct);
        Assert.Equal(video.Id, known!.VideoId);
        Assert.Equal("carol", known.AuthorId);
    }

    [Fact]
    public async Task Forgetting_a_video_cascades_likes_comments_and_counters_and_blocks_resurrection()
    {
        var video = await VideoAsync();
        var comment = await CommentAsync(video.Id);
        await Repo.AddLikeAsync(video, "alice", Ct);
        await Repo.AddLikeAsync(comment, "alice", Ct);

        await Repo.ForgetVideoAsync(video.Id, "bob", Ct);
        await Repo.ForgetVideoAsync(video.Id, "bob", Ct);
        await Repo.UpsertVideoAsync(video.Id, "bob", Ct);
        await Repo.UpsertCommentAsync(comment.Id, video.Id, "carol", Ct);

        Assert.Null(await Repo.GetVideoOwnerAsync(video.Id, Ct));
        Assert.Null(await Repo.GetCommentAsync(comment.Id, Ct));
        Assert.Equal(0, await ScalarAsync("select count(*) from likes"));
        Assert.Equal(0, await ScalarAsync("select count(*) from like_counts"));
        Assert.False(await Repo.AddLikeAsync(video, "alice", Ct));
    }

    [Fact]
    public async Task Forgetting_a_comment_removes_its_likes_and_leaves_a_tombstone()
    {
        var video = await VideoAsync();
        var comment = await CommentAsync(video.Id);
        var other = await CommentAsync(video.Id);
        await Repo.AddLikeAsync(comment, "alice", Ct);
        await Repo.AddLikeAsync(other, "alice", Ct);

        await Repo.ForgetCommentAsync(comment.Id, video.Id, Ct);
        await Repo.ForgetCommentAsync(comment.Id, video.Id, Ct);
        await Repo.UpsertCommentAsync(comment.Id, video.Id, "carol", Ct);

        Assert.Null(await Repo.GetCommentAsync(comment.Id, Ct));
        Assert.NotNull(await Repo.GetCommentAsync(other.Id, Ct));
        Assert.Equal(1, await ScalarAsync("select count(*) from likes where target_type = 'comment'"));
        Assert.Equal(1, await ScalarAsync("select count(*) from like_counts where target_type = 'comment'"));
    }

    [Fact]
    public async Task Keyset_paging_walks_all_likers_newest_first()
    {
        var video = await VideoAsync();
        var users = Enumerable.Range(0, 5).Select(i => $"user-{i}").ToList();
        foreach (var user in users)
        {
            await Repo.AddLikeAsync(video, user, Ct);
            await Task.Delay(5, Ct);
        }

        var page1 = await Repo.ListLikersAsync(video.Id, null, 2, Ct);
        var page2 = await Repo.ListLikersAsync(video.Id, new LikerCursor(page1[^1].LikedAt, page1[^1].UserId), 2, Ct);
        var page3 = await Repo.ListLikersAsync(video.Id, new LikerCursor(page2[^1].LikedAt, page2[^1].UserId), 2, Ct);

        users.Reverse();
        Assert.Equal(users, page1.Concat(page2).Concat(page3).Select(l => l.UserId));
        Assert.Single(page3);
        Assert.Equal(5, await Repo.CountVideoLikesAsync(video.Id, Ct));
    }
}
