using Maksov.VideoService.BuildingBlocks.Outbox;
using Maksov.VideoService.Comments.Bll;
using Maksov.VideoService.Comments.Bll.Ports;
using Npgsql;

namespace Maksov.VideoService.Comments.Dal.Postgres;

internal sealed class PostgresCommentRepository(NpgsqlDataSource dataSource) : ICommentRepository
{
    public async Task UpsertVideoAsync(Guid videoId, string ownerId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "insert into known_videos (video_id, owner_id) values ($1, $2) on conflict (video_id) do nothing");
        cmd.Parameters.AddWithValue(videoId);
        cmd.Parameters.AddWithValue(ownerId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ForgetVideoAsync(Guid videoId, string? ownerId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var del = new NpgsqlCommand("delete from comments where video_id = $1", connection, tx))
        {
            del.Parameters.AddWithValue(videoId);
            await del.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var mark = new NpgsqlCommand("""
            insert into known_videos (video_id, owner_id, deleted_at) values ($1, $2, now())
            on conflict (video_id) do update set deleted_at = coalesce(known_videos.deleted_at, now())
            """, connection, tx))
        {
            mark.Parameters.AddWithValue(videoId);
            mark.Parameters.AddWithValue(ownerId ?? string.Empty);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<string?> GetVideoOwnerAsync(Guid videoId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("select owner_id from known_videos where video_id = $1 and deleted_at is null");
        cmd.Parameters.AddWithValue(videoId);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task InsertAsync(NewComment comment, string videoOwnerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);
        ArgumentNullException.ThrowIfNull(videoOwnerId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var cmd = new NpgsqlCommand("""
            insert into comments (id, video_id, author_id, author_username, text, created_at)
            values ($1, $2, $3, $4, $5, $6)
            """, connection, tx))
        {
            cmd.Parameters.AddWithValue(comment.Id);
            cmd.Parameters.AddWithValue(comment.VideoId);
            cmd.Parameters.AddWithValue(comment.AuthorId);
            cmd.Parameters.AddWithValue(comment.AuthorUsername);
            cmd.Parameters.AddWithValue(comment.Text);
            cmd.Parameters.AddWithValue(comment.CreatedAt.UtcDateTime);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var eventId = Guid.CreateVersion7();
        var payload = CommentEvents.AddedPayload(
            eventId, comment.CreatedAt, comment.Id, comment.VideoId, comment.AuthorId, videoOwnerId, comment.Text.Length);
        await OutboxStore.EnqueueAsync(
            connection, tx, eventId, CommentEvents.AddedTopic, payload, CommentEvents.SubscribersOf(CommentEvents.AddedTopic), cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CommentRecord>> ListAsync(Guid videoId, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            select id, video_id, author_id, author_username, text, created_at
              from comments
             where video_id = $1
               and ($2::timestamptz is null or (created_at, id) < ($2, $3))
             order by created_at desc, id desc
             limit $4
            """);
        cmd.Parameters.AddWithValue(videoId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = after is null ? DBNull.Value : after.CreatedAt.UtcDateTime, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = after?.Id ?? Guid.Empty, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        cmd.Parameters.AddWithValue(limit);
        return await ReadAsync(cmd, cancellationToken);
    }

    public async Task<int> CountAsync(Guid videoId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("select count(*)::int from comments where video_id = $1");
        cmd.Parameters.AddWithValue(videoId);
        return (int)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    public async Task<CommentRecord?> GetAsync(Guid videoId, Guid commentId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            select id, video_id, author_id, author_username, text, created_at
              from comments where video_id = $1 and id = $2
            """);
        cmd.Parameters.AddWithValue(videoId);
        cmd.Parameters.AddWithValue(commentId);
        var rows = await ReadAsync(cmd, cancellationToken);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<bool> DeleteAsync(Guid videoId, Guid commentId, string deletedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deletedBy);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        string? authorId;
        await using (var cmd = new NpgsqlCommand("delete from comments where video_id = $1 and id = $2 returning author_id", connection, tx))
        {
            cmd.Parameters.AddWithValue(videoId);
            cmd.Parameters.AddWithValue(commentId);
            authorId = await cmd.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (authorId is null)
        {
            // Already gone: a repeat emits no second event.
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        var eventId = Guid.CreateVersion7();
        var payload = CommentEvents.DeletedPayload(eventId, TimeProvider.System.GetUtcNow(), commentId, videoId, authorId, deletedBy);
        await OutboxStore.EnqueueAsync(
            connection, tx, eventId, CommentEvents.DeletedTopic, payload, CommentEvents.SubscribersOf(CommentEvents.DeletedTopic), cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<IReadOnlyList<CommentRecord>> ReadAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        var result = new List<CommentRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CommentRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc))));
        }
        return result;
    }
}
