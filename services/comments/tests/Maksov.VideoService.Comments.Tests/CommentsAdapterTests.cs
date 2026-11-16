using System.Text.Json;
using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.Comments.Bll;
using Maksov.VideoService.Comments.Bll.Ports;
using Maksov.VideoService.Comments.Dal;
using Maksov.VideoService.Comments.Dal.Postgres;
using Maksov.VideoService.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Maksov.VideoService.Comments.Tests;

/// <summary>Postgres adapter: pagination, tombstone of a deleted video, comment cascade.</summary>
public sealed class CommentsAdapterTests : IAsyncLifetime
{
    private static readonly string[] BothSubscribers = ["lake", "likes"];

    private NpgsqlDataSource? _db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabases.CreateAsync("comments_adapter", Ct);
        if (_db is null)
        {
            return;
        }

        await new SqlMigrator(_db, NullLogger<SqlMigrator>.Instance).MigrateAsync(DalServiceCollectionExtensions.MigrationsAssembly, Ct);
        await using var cmd = _db.CreateCommand("truncate known_videos, outbox cascade");
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgresCommentRepository Repo
    {
        get
        {
            if (_db is null)
            {
                Assert.Skip("VIDEOSERVICE_TEST_POSTGRES is not set");
            }
            return new PostgresCommentRepository(_db!);
        }
    }

    private static NewComment Comment(Guid videoId, string author, string text, DateTimeOffset at) =>
        new(Guid.CreateVersion7(), videoId, author, author + "-name", text, at);

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
    public async Task Keyset_paging_walks_all_comments_newest_first()
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertVideoAsync(id, "bob", Ct);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var c = Comment(id, "alice", $"c{i}", t0.AddSeconds(i));
            ids.Add(c.Id);
            await Repo.InsertAsync(c, "bob", Ct);
        }

        var page1 = await Repo.ListAsync(id, null, 2, Ct);
        var page2 = await Repo.ListAsync(id, new PageCursor(page1[^1].CreatedAt, page1[^1].Id), 2, Ct);
        var page3 = await Repo.ListAsync(id, new PageCursor(page2[^1].CreatedAt, page2[^1].Id), 2, Ct);

        Assert.Equal(5, await Repo.CountAsync(id, Ct));
        ids.Reverse();
        Assert.Equal(ids, page1.Concat(page2).Concat(page3).Select(c => c.Id));
        Assert.Single(page3);
    }

    [Fact]
    public async Task Forgetting_a_video_removes_comments_and_blocks_resurrection()
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertVideoAsync(id, "bob", Ct);
        var c = Comment(id, "alice", "hello", DateTimeOffset.UtcNow);
        await Repo.InsertAsync(c, "bob", Ct);

        await Repo.ForgetVideoAsync(id, "bob", Ct);
        await Repo.ForgetVideoAsync(id, "bob", Ct);
        await Repo.UpsertVideoAsync(id, "bob", Ct);

        Assert.Null(await Repo.GetVideoOwnerAsync(id, Ct));
        Assert.Null(await Repo.GetAsync(id, c.Id, Ct));
        Assert.Equal(0, await Repo.CountAsync(id, Ct));
    }

    [Fact]
    public async Task Delete_removes_only_the_given_comment()
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertVideoAsync(id, "bob", Ct);
        var a = Comment(id, "alice", "a", DateTimeOffset.UtcNow);
        var b = Comment(id, "carol", "b", DateTimeOffset.UtcNow);
        await Repo.InsertAsync(a, "bob", Ct);
        await Repo.InsertAsync(b, "bob", Ct);

        Assert.True(await Repo.DeleteAsync(id, a.Id, "bob", Ct));
        Assert.False(await Repo.DeleteAsync(id, a.Id, "bob", Ct));
        Assert.NotNull(await Repo.GetAsync(id, b.Id, Ct));
    }

    private async Task<List<(string Subscriber, string Topic, string Payload)>> OutboxRowsAsync()
    {
        await using var cmd = _db!.CreateCommand("select subscriber, topic, payload::text from outbox order by created_at, subscriber");
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string, string)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    [Fact]
    public async Task Adding_a_comment_writes_one_outbox_row_per_subscriber_without_the_text()
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertVideoAsync(id, "bob", Ct);
        var c = Comment(id, "alice", "very private words", DateTimeOffset.UtcNow);

        await Repo.InsertAsync(c, "bob", Ct);

        var rows = await OutboxRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(BothSubscribers, rows.Select(r => r.Subscriber).Order());
        Assert.All(rows, r => Assert.Equal("comment.added.v1", r.Topic));
        Assert.All(rows, r => Assert.DoesNotContain("private", r.Payload, StringComparison.Ordinal));
        var payload = JsonElement.Parse(rows[0].Payload);
        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(c.Id, payload.GetProperty("commentId").GetGuid());
        Assert.Equal(id, payload.GetProperty("videoId").GetGuid());
        Assert.Equal("alice", payload.GetProperty("authorId").GetString());
        Assert.Equal("bob", payload.GetProperty("videoOwnerId").GetString());
        Assert.Equal(18, payload.GetProperty("textLength").GetInt32());
        Assert.Single(rows.Select(r => JsonElement.Parse(r.Payload).GetProperty("eventId").GetGuid()).Distinct());
    }

    [Fact]
    public async Task Deleting_a_comment_writes_one_outbox_row_per_subscriber_and_a_repeat_writes_none()
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertVideoAsync(id, "bob", Ct);
        var c = Comment(id, "alice", "to be removed", DateTimeOffset.UtcNow);
        await Repo.InsertAsync(c, "bob", Ct);

        Assert.True(await Repo.DeleteAsync(id, c.Id, "bob", Ct));
        Assert.False(await Repo.DeleteAsync(id, c.Id, "bob", Ct));

        var deleted = (await OutboxRowsAsync()).Where(r => r.Topic == "comment.deleted.v1").ToList();
        Assert.Equal(2, deleted.Count);
        Assert.Equal(BothSubscribers, deleted.Select(r => r.Subscriber).Order());
        Assert.All(deleted, r => Assert.DoesNotContain("removed", r.Payload, StringComparison.Ordinal));
        var payload = JsonElement.Parse(deleted[0].Payload);
        Assert.Equal(c.Id, payload.GetProperty("commentId").GetGuid());
        Assert.Equal("alice", payload.GetProperty("authorId").GetString());
        Assert.Equal("bob", payload.GetProperty("deletedBy").GetString());
    }

    [Fact]
    public async Task Forgetting_a_video_emits_no_per_comment_events()
    {
        var id = Guid.CreateVersion7();
        await Repo.UpsertVideoAsync(id, "bob", Ct);
        await Repo.InsertAsync(Comment(id, "alice", "hi", DateTimeOffset.UtcNow), "bob", Ct);

        await Repo.ForgetVideoAsync(id, "bob", Ct);

        Assert.DoesNotContain(await OutboxRowsAsync(), r => r.Topic == "comment.deleted.v1");
    }

    [Fact]
    public async Task A_failing_insert_writes_no_outbox_row()
    {
        // The video is unknown, so the foreign key rejects the comment; the transaction must leave no event behind.
        var c = Comment(Guid.CreateVersion7(), "alice", "orphan", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<PostgresException>(() => Repo.InsertAsync(c, "bob", Ct));

        Assert.Empty(await OutboxRowsAsync());
    }
}
