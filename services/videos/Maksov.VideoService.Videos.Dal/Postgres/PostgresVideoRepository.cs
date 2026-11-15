using Dapper;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Domain;
using Npgsql;

namespace Maksov.VideoService.Videos.Dal.Postgres;

internal sealed class PostgresVideoRepository(NpgsqlDataSource dataSource) : IVideoRepository
{
    private const string SelectColumns = """
        id as Id, owner_id as OwnerId, owner_username as OwnerUsername, title as Title, status as Status, source_key as SourceKey,
        content_type as ContentType, size_bytes as SizeBytes, duration_ms as DurationMs, width as Width,
        height as Height, renditions::text as RenditionsJson, failure_reason as FailureReason,
        created_at as CreatedAt, updated_at as UpdatedAt
        """;

    public async Task InsertAsync(NewVideo video, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(video);
        const string sql = """
            insert into videos (id, owner_id, owner_username, title, status, source_key, content_type, declared_size, created_at, updated_at)
            values (@Id, @OwnerId, @OwnerUsername, @Title, 'awaiting_upload', @SourceKey, @ContentType, @DeclaredSizeBytes, @CreatedAt, @CreatedAt)
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(sql, new
        {
            video.Id,
            video.OwnerId,
            video.OwnerUsername,
            video.Title,
            video.SourceKey,
            video.ContentType,
            video.DeclaredSizeBytes,
            CreatedAt = video.CreatedAt.UtcDateTime,
        }, cancellationToken: cancellationToken));
    }

    public async Task<VideoRecord?> GetOwnedAsync(Guid id, string ownerId, CancellationToken cancellationToken)
    {
        var sql = $"select {SelectColumns} from videos where id = @id and owner_id = @ownerId and status <> 'deleted'";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { id, ownerId }, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<VideoRecord?> GetVisibleAsync(Guid id, string viewerId, CancellationToken cancellationToken)
    {
        var sql = $"""
            select {SelectColumns} from videos
            where id = @id and status <> 'deleted' and (owner_id = @viewerId or status = 'ready')
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { id, viewerId }, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<VideoRecord>> ListOwnedAsync(string ownerId, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        var sql = after is null
            ? $"select {SelectColumns} from videos where owner_id = @ownerId and status <> 'deleted' order by created_at desc, id desc limit @limit"
            : $"""
               select {SelectColumns} from videos
               where owner_id = @ownerId and status <> 'deleted' and (created_at, id) < (@createdAt, @id)
               order by created_at desc, id desc limit @limit
               """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            ownerId,
            limit,
            createdAt = after?.CreatedAt.UtcDateTime,
            id = after?.Id,
        }, cancellationToken: cancellationToken));
        return rows.Select(static r => r.ToRecord()).ToList();
    }

    public async Task<IReadOnlyList<VideoRecord>> ListReadyByOwnersAsync(
        IReadOnlyCollection<string> ownerIds, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownerIds);
        if (ownerIds.Count == 0)
        {
            return [];
        }

        var sql = after is null
            ? $"select {SelectColumns} from videos where owner_id = any(@owners) and status = 'ready' order by created_at desc, id desc limit @limit"
            : $"""
               select {SelectColumns} from videos
               where owner_id = any(@owners) and status = 'ready' and (created_at, id) < (@createdAt, @id)
               order by created_at desc, id desc limit @limit
               """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(sql, new
        {
            owners = ownerIds.ToArray(),
            limit,
            createdAt = after?.CreatedAt.UtcDateTime,
            id = after?.Id,
        }, cancellationToken: cancellationToken));
        return rows.Select(static r => r.ToRecord()).ToList();
    }

    public async Task<bool> MarkUploadedAndEnqueueAsync(Guid id, long sizeBytes, string? traceParent, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var updated = await connection.ExecuteAsync(new CommandDefinition("""
            update videos set status = 'uploaded', size_bytes = @sizeBytes, updated_at = now()
            where id = @id and status = 'awaiting_upload'
            """, new { id, sizeBytes }, tx, cancellationToken: cancellationToken));

        if (updated == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            insert into transcode_jobs (id, video_id, status, trace_parent)
            values (@jobId, @id, 'queued', @traceParent)
            on conflict (video_id) do nothing;
            select pg_notify('transcode_jobs', @id::text);
            """, new { jobId = Guid.CreateVersion7(), id, traceParent }, tx, cancellationToken: cancellationToken));

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkFailedAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            update videos set status = 'failed', failure_reason = @reason, updated_at = now()
            where id = @id and status in ('awaiting_upload', 'uploaded', 'processing')
            """, new { id, reason }, cancellationToken: cancellationToken));
    }

    public async Task<bool> DeleteOwnedAsync(Guid id, string ownerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        var deleted = await connection.ExecuteAsync(new CommandDefinition("""
            update videos set status = 'deleted', deleted_at = now(), updated_at = now()
            where id = @id and owner_id = @ownerId and status <> 'deleted'
            """, new { id, ownerId }, tx, cancellationToken: cancellationToken));

        if (deleted == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        // The transcode job is no longer needed; the worker will notice one already in progress because the video did not become ready.
        await connection.ExecuteAsync(new CommandDefinition("""
            update transcode_jobs set status = 'failed', locked_until = null, last_error = 'video deleted', updated_at = now()
            where video_id = @id and status in ('queued', 'running')
            """, new { id }, tx, cancellationToken: cancellationToken));

        // The "video deleted" event: one row per subscriber, in the same transaction (outbox).
        var eventId = Guid.CreateVersion7();
        await connection.ExecuteAsync(new CommandDefinition("""
            insert into outbox (id, subscriber, topic, payload)
            select @eventId, s.subscriber, @topic,
                   jsonb_build_object('eventId', @eventId, 'occurredAt', now(), 'schemaVersion', 1,
                                      'videoId', @id, 'ownerId', @ownerId)
              from unnest(@subscribers) as s(subscriber);
            select pg_notify('outbox', @eventId::text);
            """, new
        {
            eventId,
            topic = VideoEvents.Deleted,
            id,
            ownerId,
            subscribers = VideoEvents.SubscribersOf(VideoEvents.Deleted).ToArray(),
        }, tx, cancellationToken: cancellationToken));

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string OwnerId { get; init; } = "";
        public string OwnerUsername { get; init; } = "";
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
        public string SourceKey { get; init; } = "";
        public string ContentType { get; init; } = "";
        public long? SizeBytes { get; init; }
        public int? DurationMs { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public string? RenditionsJson { get; init; }
        public string? FailureReason { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }

        public VideoRecord ToRecord() => new(
            Id, OwnerId, Title, VideoStatusExtensions.ParseWire(Status), SourceKey, ContentType, SizeBytes,
            DurationMs, Width, Height, RenditionsJson, FailureReason,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)),
            OwnerUsername);
    }
}
