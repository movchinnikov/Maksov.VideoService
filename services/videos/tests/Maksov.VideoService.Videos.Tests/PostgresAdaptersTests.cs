using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.TestSupport;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Dal.Postgres;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Adapters.Postgres;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Maksov.VideoService.Videos.Tests;

/// <summary>
/// Postgres adapters (migrations, API repository, worker queue) on a real DB.
/// Connection string — VIDEOSERVICE_TEST_POSTGRES (make test-dotnet starts a temporary container).
/// Without it the tests are skipped.
/// </summary>
public sealed class PostgresAdaptersTests : IAsyncLifetime
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("VIDEOSERVICE_TEST_POSTGRES");
    private NpgsqlDataSource? _dataSource;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (ConnectionString is null)
        {
            return;
        }

        _dataSource = NpgsqlDataSource.Create(ConnectionString);
        await new SqlMigrator(_dataSource, NullLogger<SqlMigrator>.Instance).MigrateAsync(typeof(PostgresVideoRepository).Assembly, Ct);
        await using var cmd = _dataSource.CreateCommand("truncate transcode_jobs, videos");
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }
    }

    private NpgsqlDataSource Db
    {
        get
        {
            if (_dataSource is null)
            {
                Assert.Skip("VIDEOSERVICE_TEST_POSTGRES is not set");
            }
            return _dataSource!;
        }
    }

    [Fact]
    public async Task Upload_to_ready_happy_path()
    {
        var videos = new PostgresVideoRepository(Db);
        var queue = new PostgresJobQueue(Db);
        var id = await CreateUploadedAsync(videos, "alice");

        var job = await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.NotNull(job);
        Assert.Equal(id, job.VideoId);
        Assert.Equal(1, job.Attempt);

        Assert.Equal(MediaKeys.Source(id), await queue.StartProcessingAsync(job, Ct));
        Assert.True(await queue.ExtendLeaseAsync(job, TimeSpan.FromMinutes(2), Ct));

        var ladder = RenditionLadder.Plan(1280, 720, 30);
        await queue.CompleteAsync(job, new ProcessedVideo(6000, 1280, 720, ladder), Ct);

        var video = await videos.GetOwnedAsync(id, "alice", Ct);
        Assert.Equal(VideoStatus.Ready, video!.Status);
        Assert.Equal(6000, video.DurationMs);
        Assert.Contains("\"720p\"", video.RenditionsJson, StringComparison.Ordinal);
        Assert.Null(await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct));
    }

    [Fact]
    public async Task Complete_twice_enqueues_once()
    {
        var videos = new PostgresVideoRepository(Db);
        var id = Guid.CreateVersion7();
        await videos.InsertAsync(new NewVideo(id, "bob", "t", MediaKeys.Source(id), "video/mp4", 10, DateTimeOffset.UtcNow), Ct);

        Assert.True(await videos.MarkUploadedAndEnqueueAsync(id, 10, null, Ct));
        Assert.False(await videos.MarkUploadedAndEnqueueAsync(id, 10, null, Ct));

        await using var cmd = Db.CreateCommand($"select count(*) from transcode_jobs where video_id = '{id}'");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_stale_worker_cannot_finish()
    {
        var videos = new PostgresVideoRepository(Db);
        var crashed = new PostgresJobQueue(Db);
        var id = await CreateUploadedAsync(videos, "carol");

        var first = await crashed.TryDequeueAsync(TimeSpan.FromMilliseconds(1), Ct);
        Assert.NotNull(first);
        await Task.Delay(50, Ct);

        // A second worker (different locked_by) takes the task with an expired lease.
        await using var otherDb = NpgsqlDataSource.Create(ConnectionString!);
        var other = new PostgresJobQueue(otherDb, "other-worker");
        var second = await other.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.NotNull(second);
        Assert.Equal(2, second.Attempt);

        // The first worker "came back to life": the lease no longer belongs to it.
        Assert.False(await crashed.ExtendLeaseAsync(first, TimeSpan.FromMinutes(2), Ct));
    }

    [Fact]
    public async Task Rejected_media_fails_video_with_reason()
    {
        var videos = new PostgresVideoRepository(Db);
        var queue = new PostgresJobQueue(Db);
        var id = await CreateUploadedAsync(videos, "dave");
        var job = (await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct))!;
        await queue.StartProcessingAsync(job, Ct);

        await queue.FailAsync(job, "duration_exceeded", "too long", Ct);

        var video = await videos.GetOwnedAsync(id, "dave", Ct);
        Assert.Equal(VideoStatus.Failed, video!.Status);
        Assert.Equal("duration_exceeded", video.FailureReason);
    }

    [Fact]
    public async Task Retry_and_release_return_job_to_queue()
    {
        var videos = new PostgresVideoRepository(Db);
        var queue = new PostgresJobQueue(Db);
        await CreateUploadedAsync(videos, "erin");

        var job = (await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct))!;
        await queue.ReleaseAsync(job, Ct);
        var again = (await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct))!;
        Assert.Equal(1, again.Attempt); // graceful stop does not consume an attempt

        await queue.RetryAsync(again, "boom", TimeSpan.FromHours(1), Ct);
        Assert.Null(await queue.TryDequeueAsync(TimeSpan.FromMinutes(2), Ct)); // waits for backoff
    }

    [Fact]
    public async Task Listener_wakes_up_on_enqueue()
    {
        var videos = new PostgresVideoRepository(Db);
        await using var signal = await new PostgresJobQueue(Db).ListenAsync(Ct);

        var wait = signal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await CreateUploadedAsync(videos, "frank");

        var finished = await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.Same(wait, finished);
    }

    [Fact]
    public async Task Keyset_paging_returns_newest_first_without_overlap()
    {
        var videos = new PostgresVideoRepository(Db);
        var now = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            await videos.InsertAsync(new NewVideo(id, "grace", $"v{i}", MediaKeys.Source(id), "video/mp4", 1, now.AddSeconds(i)), Ct);
        }

        var page1 = await videos.ListOwnedAsync("grace", null, 3, Ct);
        var page2 = await videos.ListOwnedAsync("grace", new PageCursor(page1[^1].CreatedAt, page1[^1].Id), 3, Ct);

        Assert.Equal(ids.AsEnumerable().Reverse(), page1.Concat(page2).Select(v => v.Id));
    }

    private static async Task<Guid> CreateUploadedAsync(PostgresVideoRepository videos, string owner)
    {
        var id = Guid.CreateVersion7();
        await videos.InsertAsync(new NewVideo(id, owner, "t", MediaKeys.Source(id), "video/mp4", 10, DateTimeOffset.UtcNow), Ct);
        Assert.True(await videos.MarkUploadedAndEnqueueAsync(id, 10, null, Ct));
        return id;
    }
}
