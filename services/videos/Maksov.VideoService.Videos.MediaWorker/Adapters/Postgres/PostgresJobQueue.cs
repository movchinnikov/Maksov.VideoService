using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Npgsql;

namespace Maksov.VideoService.Videos.MediaWorker.Adapters.Postgres;

/// <summary>
/// Postgres job queue: <c>FOR UPDATE SKIP LOCKED</c> (competing workers do not block each other),
/// lease via <c>locked_until</c>, wake-up via <c>LISTEN/NOTIFY</c>. All job updates check <c>locked_by</c>.
/// </summary>
internal sealed class PostgresJobQueue(NpgsqlDataSource dataSource, string? workerId = null) : IJobQueue
{
    private readonly string _workerId = workerId ?? $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task<IJobSignal> ListenAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var cmd = new NpgsqlCommand("listen transcode_jobs", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        return new Signal(connection);
    }

    public async Task<TranscodeJob?> TryDequeueAsync(TimeSpan lease, CancellationToken cancellationToken)
    {
        const string sql = """
            update transcode_jobs j
               set status = 'running', attempts = j.attempts + 1, locked_by = $1,
                   locked_until = now() + $2, updated_at = now()
             where j.id = (
                   select id from transcode_jobs
                    where (status = 'queued' and run_after <= now())
                       or (status = 'running' and locked_until < now())
                    order by run_after
                    limit 1
                    for update skip locked)
            returning j.id, j.video_id, j.attempts, j.max_attempts, j.trace_parent
            """;

        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(lease);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TranscodeJob(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task<bool> ExtendLeaseAsync(TranscodeJob job, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            update transcode_jobs set locked_until = now() + $3, updated_at = now()
            where id = $1 and locked_by = $2 and status = 'running'
            """);
        cmd.Parameters.AddWithValue(job.Id);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(lease);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<string?> StartProcessingAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            update videos set status = 'processing', updated_at = now()
            where id = $1 and status in ('uploaded', 'processing')
            returning source_key
            """);
        cmd.Parameters.AddWithValue(job.VideoId);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<bool> CompleteAsync(TranscodeJob job, ProcessedVideo result, CancellationToken cancellationToken)
    {
        var renditions = JsonSerializer.Serialize(
            result.Renditions.Select(static r => new RenditionJson(r.Name, r.Width, r.Height, r.VideoKbps)).ToList(),
            WorkerJsonContext.Default.ListRenditionJson);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var updated = await ExecuteAsync(connection, tx, """
            update videos set status = 'ready', duration_ms = $2, width = $3, height = $4,
                              renditions = $5::jsonb, failure_reason = null, updated_at = now()
            where id = $1 and status = 'processing'
            """, cancellationToken, job.VideoId, result.DurationMs, result.Width, result.Height, renditions);

        if (updated > 0)
        {
            // "Video processed" events go in the same transaction (outbox), one row per subscriber:
            // they are not lost and not sent without the status change.
            var eventId = Guid.CreateVersion7();
            await ExecuteAsync(connection, tx, """
                insert into outbox (id, subscriber, topic, payload, trace_parent)
                select $1::uuid, s.subscriber, $7::text,
                       jsonb_build_object('eventId', $1::uuid, 'occurredAt', now(), 'schemaVersion', 1, 'traceId', $2::text,
                                          'videoId', v.id, 'ownerId', v.owner_id, 'sourceKey', v.source_key,
                                          'durationMs', $3::int, 'width', $4::int, 'height', $5::int),
                       $2::text
                  from videos v, unnest($8::text[]) as s(subscriber)
                 where v.id = $6::uuid
                """, cancellationToken,
                eventId, (object?)job.TraceParent ?? DBNull.Value, result.DurationMs, result.Width, result.Height, job.VideoId,
                VideoEvents.Processed, VideoEvents.SubscribersOf(VideoEvents.Processed).ToArray());
            await ExecuteAsync(connection, tx, "select pg_notify('outbox', $1::text)", cancellationToken, eventId.ToString());
        }

        await ExecuteAsync(connection, tx, """
            update transcode_jobs
               set status = 'succeeded', locked_until = null, last_error = $3::text, updated_at = now()
             where id = $1 and locked_by = $2
            """, cancellationToken, job.Id, _workerId, updated > 0 ? (object)DBNull.Value : "skipped: video is no longer awaiting publication");

        await tx.CommitAsync(cancellationToken);
        return updated > 0;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = p });
        }
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SkipAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(cancellationToken, FinishJob(job, "succeeded", "skipped: video is not awaiting processing"));
    }

    public async Task RetryAsync(TranscodeJob job, string details, TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            update transcode_jobs
               set status = 'queued', run_after = now() + $3, locked_until = null, locked_by = null,
                   last_error = left($4, 2000), updated_at = now()
             where id = $1 and locked_by = $2
            """);
        cmd.Parameters.AddWithValue(job.Id);
        cmd.Parameters.AddWithValue(_workerId);
        cmd.Parameters.AddWithValue(delay);
        cmd.Parameters.AddWithValue(details);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailAsync(TranscodeJob job, string reasonCode, string? details, CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(cancellationToken, Command("""
            update videos set status = 'failed', failure_reason = $2, updated_at = now()
            where id = $1 and status in ('uploaded', 'processing')
            """, job.VideoId, reasonCode),
            FinishJob(job, "failed", details ?? reasonCode));
    }

    public async Task ReleaseAsync(TranscodeJob job, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            update transcode_jobs
               set status = 'queued', attempts = greatest(attempts - 1, 0), locked_until = null, locked_by = null,
                   run_after = now(), updated_at = now()
             where id = $1 and locked_by = $2 and status = 'running';
            """);
        cmd.Parameters.AddWithValue(job.Id);
        cmd.Parameters.AddWithValue(_workerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlBatchCommand FinishJob(TranscodeJob job, string status, string? error) => Command("""
        update transcode_jobs
           set status = $3, locked_until = null, last_error = left($4, 2000), updated_at = now()
         where id = $1 and locked_by = $2
        """, job.Id, _workerId, status, (object?)error ?? DBNull.Value);

    private static NpgsqlBatchCommand Command(string sql, params object[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var p in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = p });
        }
        return command;
    }

    /// <summary>Several commands in one network round-trip within one transaction.</summary>
    private async Task ExecuteInTransactionAsync(CancellationToken cancellationToken, params NpgsqlBatchCommand[] commands)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, tx);
        foreach (var command in commands)
        {
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private sealed class Signal(NpgsqlConnection connection) : IJobSignal
    {
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            connection.WaitAsync(timeout, cancellationToken);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

internal sealed record RenditionJson(string Name, int Width, int Height, int Kbps);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<RenditionJson>))]
internal sealed partial class WorkerJsonContext : JsonSerializerContext;
