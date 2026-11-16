using System.Text.Json;
using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.TestSupport;
using Maksov.VideoService.Videos.Dal.Postgres;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Adapters.Postgres;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Maksov.VideoService.Videos.Tests;

/// <summary>
/// Outbox of the videos service: the "video processed" event is written in the same transaction as the ready status,
/// and delivered by the dispatcher with locking and retries.
/// </summary>
public sealed class OutboxTests : IAsyncLifetime
{
    private NpgsqlDataSource? _db;

    private static readonly string[] Subs = [VideoEvents.Analysis, VideoEvents.Comments, VideoEvents.Likes];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabases.CreateAsync("videos_outbox", Ct);
        if (_db is null)
        {
            return;
        }

        await new SqlMigrator(_db, NullLogger<SqlMigrator>.Instance)
            .MigrateAsync(Maksov.VideoService.Videos.Dal.DalServiceCollectionExtensions.MigrationsAssembly, Ct);
        await using var cmd = _db.CreateCommand("truncate outbox, transcode_jobs, videos cascade");
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    private NpgsqlDataSource Db
    {
        get
        {
            if (_db is null)
            {
                Assert.Skip("VIDEOSERVICE_TEST_POSTGRES is not set");
            }
            return _db!;
        }
    }

    private async Task<(Guid VideoId, TranscodeJob Job, PostgresJobQueue Queue)> ReadyVideoAsync(string owner)
    {
        var videos = new PostgresVideoRepository(Db);
        var queue = new PostgresJobQueue(Db);
        var id = Guid.CreateVersion7();
        await videos.InsertAsync(new Maksov.VideoService.Videos.Bll.Ports.NewVideo(id, owner, "t", MediaKeys.Source(id), "video/mp4", 10, DateTimeOffset.UtcNow), Ct);
        await videos.MarkUploadedAndEnqueueAsync(id, 10, "00-trace-span-01", Ct);
        var job = (await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct))!;
        await queue.StartProcessingAsync(job, Ct);
        await queue.CompleteAsync(job, new ProcessedVideo(16274, 720, 1280, RenditionLadder.Plan(720, 1280, 30)), Ct);
        return (id, job, queue);
    }

    [Fact]
    public async Task Completing_a_video_writes_one_event_per_subscriber_with_contract_fields()
    {
        var (videoId, _, _) = await ReadyVideoAsync("alice");

        var batch = await new PostgresOutbox(Db).ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct);

        Assert.Equal(3, batch.Count);
        Assert.Equal(Subs.Order(), batch.Select(m => m.Subscriber).Order());
        Assert.All(batch, m => Assert.Equal("video.processed.v1", m.Topic));
        var message = batch[0];
        var payload = JsonElement.Parse(message.Payload);
        Assert.Equal(1, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(message.Id, payload.GetProperty("eventId").GetGuid());
        Assert.Equal(videoId, payload.GetProperty("videoId").GetGuid());
        Assert.Equal("alice", payload.GetProperty("ownerId").GetString());
        Assert.Equal(MediaKeys.Source(videoId), payload.GetProperty("sourceKey").GetString());
        Assert.Equal(16274, payload.GetProperty("durationMs").GetInt32());
        Assert.Equal((720, 1280), (payload.GetProperty("width").GetInt32(), payload.GetProperty("height").GetInt32()));
        Assert.Equal("00-trace-span-01", payload.GetProperty("traceId").GetString());
        Assert.True(payload.TryGetProperty("occurredAt", out _));
    }

    [Fact]
    public async Task Claim_locks_messages_and_failure_applies_backoff_until_sent()
    {
        await ReadyVideoAsync("bob");
        var outbox = new PostgresOutbox(Db);

        var all = await outbox.ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(3, all.Count);
        Assert.Empty(await outbox.ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct)); // locked by another dispatcher

        var first = all.Single(m => m.Subscriber == VideoEvents.Analysis);
        var other = all.Single(m => m.Subscriber == VideoEvents.Comments);
        await outbox.MarkSentAsync(other.Id, other.Subscriber, Ct); // each subscriber has its own delivery
        var third = all.Single(m => m.Subscriber == VideoEvents.Likes);
        await outbox.MarkSentAsync(third.Id, third.Subscriber, Ct);
        await outbox.MarkFailedAsync(first.Id, first.Subscriber, "analysis down", TimeSpan.FromHours(1), Ct);
        Assert.Empty(await outbox.ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct)); // waits for the pause

        await using (var cmd = Db.CreateCommand("update outbox set next_attempt_at = now()"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var retry = Assert.Single(await outbox.ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct));
        Assert.Equal(2, retry.Attempt);

        await outbox.MarkSentAsync(retry.Id, retry.Subscriber, Ct);
        Assert.Empty(await outbox.ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct));
    }

    [Fact]
    public async Task Deleting_a_video_writes_deleted_event_per_subscriber_and_blocks_completion()
    {
        var videos = new PostgresVideoRepository(Db);
        var queue = new PostgresJobQueue(Db);
        var id = Guid.CreateVersion7();
        await videos.InsertAsync(new Maksov.VideoService.Videos.Bll.Ports.NewVideo(id, "dave", "t", MediaKeys.Source(id), "video/mp4", 10, DateTimeOffset.UtcNow), Ct);
        await videos.MarkUploadedAndEnqueueAsync(id, 10, null, Ct);
        var job = (await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct))!;
        await queue.StartProcessingAsync(job, Ct);

        Assert.False(await videos.DeleteOwnedAsync(id, "mallory", Ct));
        Assert.True(await videos.DeleteOwnedAsync(id, "dave", Ct));

        var completed = await queue.CompleteAsync(job, new ProcessedVideo(1000, 720, 1280, RenditionLadder.Plan(720, 1280, 30)), Ct);
        Assert.False(completed);

        var batch = await new PostgresOutbox(Db).ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(3, batch.Count);
        Assert.All(batch, m => Assert.Equal("video.deleted.v1", m.Topic));
        Assert.Null(await videos.GetVisibleAsync(id, "dave", Ct));
    }

    [Fact]
    public async Task Failed_video_produces_no_event()
    {
        var videos = new PostgresVideoRepository(Db);
        var queue = new PostgresJobQueue(Db);
        var id = Guid.CreateVersion7();
        await videos.InsertAsync(new Maksov.VideoService.Videos.Bll.Ports.NewVideo(id, "carol", "t", MediaKeys.Source(id), "video/mp4", 10, DateTimeOffset.UtcNow), Ct);
        await videos.MarkUploadedAndEnqueueAsync(id, 10, null, Ct);
        var job = (await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct))!;
        await queue.StartProcessingAsync(job, Ct);

        await queue.FailAsync(job, "invalid_media", "bad", Ct);

        Assert.Empty(await new PostgresOutbox(Db).ClaimAsync(Subs, 10, TimeSpan.FromMinutes(1), Ct));
    }
}
