using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.Dal;
using Maksov.VideoService.Analysis.Dal.Postgres;
using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Maksov.VideoService.Analysis.Tests;

/// <summary>Postgres adapter of the analysis service (store + queue) on a real database.</summary>
public sealed class AnalysisAdapterTests : IAsyncLifetime
{
    private NpgsqlDataSource? _db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabases.CreateAsync("analysis", Ct);
        if (_db is null)
        {
            return;
        }

        await new SqlMigrator(_db, NullLogger<SqlMigrator>.Instance).MigrateAsync(DalServiceCollectionExtensions.MigrationsAssembly, Ct);
        await using var cmd = _db.CreateCommand("truncate analyses");
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

    private static AnalysisRequest Request(Guid? id = null, string owner = "alice") =>
        new(Guid.CreateVersion7(), id ?? Guid.CreateVersion7(), owner, "uploads/x/source", 16274, 720, 1280);

    [Fact]
    public async Task Enqueue_is_idempotent_and_full_cycle_produces_description()
    {
        var store = new PostgresAnalysisStore(Db);
        var request = Request();

        Assert.True(await store.EnqueueAsync(request, Ct));
        Assert.False(await store.EnqueueAsync(request with { EventId = Guid.CreateVersion7() }, Ct));
        Assert.Equal(AnalysisStatus.Queued, (await store.GetAsync(request.VideoId, Ct))!.Status);

        var job = await store.TryDequeueAsync(TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(job);
        Assert.Equal((request.VideoId, 720, 1280, 1), (job.VideoId, job.Width, job.Height, job.Attempt));
        Assert.True(await store.ExtendLeaseAsync(job, TimeSpan.FromMinutes(5), Ct));
        Assert.Equal(AnalysisStatus.Processing, (await store.GetAsync(request.VideoId, Ct))!.Status);

        await store.CompleteAsync(job, new AnalysisResult("Vertical video.", ["vertical", "with sound"], "{\"sceneCuts\":3}"), Ct);

        var done = await store.GetAsync(request.VideoId, Ct);
        Assert.Equal(AnalysisStatus.Done, done!.Status);
        Assert.Equal("Vertical video.", done.Description);
        Assert.Equal(["vertical", "with sound"], done.Tags);
        Assert.Null(await store.TryDequeueAsync(TimeSpan.FromMinutes(5), Ct));
        Assert.True(await store.DeleteAsync(request.VideoId, Ct));
        Assert.False(await store.DeleteAsync(request.VideoId, Ct));
        Assert.Null(await store.GetAsync(request.VideoId, Ct));
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_stale_worker_cannot_finish()
    {
        var crashed = new PostgresAnalysisStore(Db, "worker-1");
        var request = Request();
        await crashed.EnqueueAsync(request, Ct);
        var first = (await crashed.TryDequeueAsync(TimeSpan.FromMilliseconds(1), Ct))!;
        await Task.Delay(50, Ct);

        var other = new PostgresAnalysisStore(Db, "worker-2");
        var second = await other.TryDequeueAsync(TimeSpan.FromMinutes(5), Ct);

        Assert.NotNull(second);
        Assert.Equal(2, second.Attempt);
        Assert.False(await crashed.ExtendLeaseAsync(first, TimeSpan.FromMinutes(5), Ct));
        await crashed.CompleteAsync(first, new AnalysisResult("foreign result", [], "{}"), Ct); // must not be written
        Assert.Equal(AnalysisStatus.Processing, (await other.GetAsync(request.VideoId, Ct))!.Status);
    }

    [Fact]
    public async Task Fail_retry_and_release_behave()
    {
        var store = new PostgresAnalysisStore(Db);
        var failing = Request();
        await store.EnqueueAsync(failing, Ct);
        var job = (await store.TryDequeueAsync(TimeSpan.FromMinutes(5), Ct))!;

        await store.RetryAsync(job, "boom", TimeSpan.FromHours(1), Ct);
        Assert.Null(await store.TryDequeueAsync(TimeSpan.FromMinutes(5), Ct)); // waiting for backoff

        await using (var cmd = Db.CreateCommand("update analyses set run_after = now()"))
        {
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var again = (await store.TryDequeueAsync(TimeSpan.FromMinutes(5), Ct))!;
        await store.ReleaseAsync(again, Ct);
        var afterRelease = (await store.TryDequeueAsync(TimeSpan.FromMinutes(5), Ct))!;
        Assert.Equal(again.Attempt, afterRelease.Attempt); // a graceful stop does not spend an attempt

        await store.FailAsync(afterRelease, "analysis_error", "details", Ct);
        var failed = await store.GetAsync(failing.VideoId, Ct);
        Assert.Equal(AnalysisStatus.Failed, failed!.Status);
        Assert.Null(failed.Description);
    }

    [Fact]
    public async Task Listener_wakes_up_on_enqueue()
    {
        var store = new PostgresAnalysisStore(Db);
        await using var signal = await store.ListenAsync(Ct);

        var wait = signal.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await store.EnqueueAsync(Request(), Ct);

        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
    }
}
