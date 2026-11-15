using Maksov.VideoService.Analysis.Bll.Ports;
using Npgsql;

namespace Maksov.VideoService.Analysis.Dal.Postgres;

/// <summary>
/// Analysis store and job queue in a single table. Queue: <c>FOR UPDATE SKIP LOCKED</c> (workers do not block each other),
/// lease via <c>locked_until</c>, wake-up via <c>LISTEN/NOTIFY</c>. All job changes check <c>locked_by</c>.
/// </summary>
internal sealed class PostgresAnalysisStore(NpgsqlDataSource dataSource, string? workerId = null) : IAnalysisRepository, IAnalysisQueue
{
    private readonly string _workerId = workerId ?? $"{Environment.MachineName}:{Environment.ProcessId}";

    // ---------- IAnalysisRepository

    public async Task<bool> EnqueueAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using var insert = new NpgsqlCommand("""
            insert into analyses (video_id, owner_id, source_key, event_id, duration_ms, width, height, status)
            values ($1, $2, $3, $4, $5, $6, $7, 'queued')
            on conflict (video_id) do nothing
            returning video_id
            """, connection, tx);
        insert.Parameters.AddWithValue(request.VideoId);
        insert.Parameters.AddWithValue(request.OwnerId);
        insert.Parameters.AddWithValue(request.SourceKey);
        insert.Parameters.AddWithValue(request.EventId);
        insert.Parameters.AddWithValue(request.DurationMs);
        insert.Parameters.AddWithValue(request.Width);
        insert.Parameters.AddWithValue(request.Height);
        var created = await insert.ExecuteScalarAsync(cancellationToken) is not null;

        if (created)
        {
            await using var notify = new NpgsqlCommand("select pg_notify('analysis_jobs', $1)", connection, tx);
            notify.Parameters.AddWithValue(request.VideoId.ToString());
            await notify.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<AnalysisRecord?> GetAsync(Guid videoId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            select video_id, owner_id, status, description, tags, updated_at
            from analyses where video_id = $1
            """);
        cmd.Parameters.AddWithValue(videoId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AnalysisRecord(
            reader.GetGuid(0),
            reader.GetString(1),
            ParseStatus(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            (string[])reader.GetValue(4),
            reader.GetFieldValue<DateTime>(5) is var updated ? new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)) : default);
    }

    public async Task<bool> DeleteAsync(Guid videoId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("delete from analyses where video_id = $1");
        cmd.Parameters.AddWithValue(videoId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    // ---------- IAnalysisQueue

    public async Task<IJobSignal> ListenAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var cmd = new NpgsqlCommand("listen analysis_jobs", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        return new Signal(connection);
    }

    public async Task<AnalysisJob?> TryDequeueAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            update analyses a
               set status = 'processing', attempts = a.attempts + 1, locked_by = $1,
                   locked_until = now() + $2, updated_at = now()
             where a.video_id = (
                   select video_id from analyses
                    where (status = 'queued' and run_after <= now())
                       or (status = 'processing' and locked_until < now())
                    order by run_after
                    limit 1
                    for update skip locked)
            returning a.video_id, a.source_key, a.duration_ms, a.width, a.height, a.attempts, a.max_attempts
            """);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(lease);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AnalysisJob(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6));
    }

    public async Task<bool> ExtendLeaseAsync(AnalysisJob job, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var cmd = dataSource.CreateCommand("""
            update analyses set locked_until = now() + $3, updated_at = now()
            where video_id = $1 and locked_by = $2 and status = 'processing'
            """);
        cmd.Parameters.AddWithValue(job.VideoId);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(lease);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task CompleteAsync(AnalysisJob job, AnalysisResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);
        await using var cmd = dataSource.CreateCommand("""
            update analyses
               set status = 'done', description = $3, tags = $4, facts = $5::jsonb,
                   locked_until = null, failure_reason = null, last_error = null, updated_at = now()
             where video_id = $1 and locked_by = $2
            """);
        cmd.Parameters.AddWithValue(job.VideoId);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(result.Description);
        cmd.Parameters.AddWithValue(result.Tags.ToArray());
        cmd.Parameters.AddWithValue(result.FactsJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RetryAsync(AnalysisJob job, string details, TimeSpan delay, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var cmd = dataSource.CreateCommand("""
            update analyses
               set status = 'queued', run_after = now() + $3, locked_until = null, locked_by = null,
                   last_error = left($4, 2000), updated_at = now()
             where video_id = $1 and locked_by = $2
            """);
        cmd.Parameters.AddWithValue(job.VideoId);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(delay);
        cmd.Parameters.AddWithValue(details);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailAsync(AnalysisJob job, string reasonCode, string? details, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var cmd = dataSource.CreateCommand("""
            update analyses
               set status = 'failed', failure_reason = $3, last_error = left($4, 2000), locked_until = null, updated_at = now()
             where video_id = $1 and locked_by = $2
            """);
        cmd.Parameters.AddWithValue(job.VideoId);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(reasonCode);
        cmd.Parameters.AddWithValue((object?)details ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseAsync(AnalysisJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using var cmd = dataSource.CreateCommand("""
            update analyses
               set status = 'queued', attempts = greatest(attempts - 1, 0), locked_until = null, locked_by = null,
                   run_after = now(), updated_at = now()
             where video_id = $1 and locked_by = $2 and status = 'processing'
            """);
        cmd.Parameters.AddWithValue(job.VideoId);
        cmd.Parameters.AddWithValue(_workerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AnalysisStatus ParseStatus(string value) => value switch
    {
        "queued" => AnalysisStatus.Queued,
        "processing" => AnalysisStatus.Processing,
        "done" => AnalysisStatus.Done,
        "failed" => AnalysisStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown analysis status"),
    };

    private sealed class Signal(NpgsqlConnection connection) : IJobSignal
    {
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            connection.WaitAsync(timeout, cancellationToken);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
