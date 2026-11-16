using System.Diagnostics;
using Maksov.VideoService.BuildingBlocks.Outbox;
using Maksov.VideoService.Likes.Bll;
using Maksov.VideoService.Likes.Bll.Ports;
using Npgsql;
using NpgsqlTypes;

namespace Maksov.VideoService.Likes.Dal.Postgres;

internal sealed class PostgresLikeRepository(NpgsqlDataSource dataSource) : ILikeRepository
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

        // The tombstone goes first: it takes the row lock that concurrent likes wait on ("for share").
        await using (var mark = new NpgsqlCommand("""
            insert into known_videos (video_id, owner_id, deleted_at) values ($1, $2, now())
            on conflict (video_id) do update set deleted_at = coalesce(known_videos.deleted_at, now())
            """, connection, tx))
        {
            mark.Parameters.AddWithValue(videoId);
            mark.Parameters.AddWithValue(ownerId ?? string.Empty);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteAsync(connection, tx, """
            delete from like_counts
             where (target_type = 'video' and target_id = $1)
                or (target_type = 'comment' and target_id in (select target_id from likes where video_id = $1 and target_type = 'comment'))
            """, videoId, cancellationToken);
        await ExecuteAsync(connection, tx, "delete from likes where video_id = $1", videoId, cancellationToken);
        await ExecuteAsync(connection, tx, "delete from known_comments where video_id = $1", videoId, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<string?> GetVideoOwnerAsync(Guid videoId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("select owner_id from known_videos where video_id = $1 and deleted_at is null");
        cmd.Parameters.AddWithValue(videoId);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task UpsertCommentAsync(Guid commentId, Guid videoId, string authorId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            insert into known_comments (comment_id, video_id, author_id)
            select $1, $2, $3
             where not exists (select 1 from known_videos where video_id = $2 and deleted_at is not null)
            on conflict (comment_id) do nothing
            """);
        cmd.Parameters.AddWithValue(commentId);
        cmd.Parameters.AddWithValue(videoId);
        cmd.Parameters.AddWithValue(authorId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ForgetCommentAsync(Guid commentId, Guid videoId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var mark = new NpgsqlCommand("""
            insert into known_comments (comment_id, video_id, author_id, deleted_at) values ($1, $2, '', now())
            on conflict (comment_id) do update set deleted_at = coalesce(known_comments.deleted_at, now())
            """, connection, tx))
        {
            mark.Parameters.AddWithValue(commentId);
            mark.Parameters.AddWithValue(videoId);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteAsync(connection, tx, "delete from like_counts where target_type = 'comment' and target_id = $1", commentId, cancellationToken);
        await ExecuteAsync(connection, tx, "delete from likes where target_type = 'comment' and target_id = $1", commentId, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<KnownComment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "select comment_id, video_id, author_id from known_comments where comment_id = $1 and deleted_at is null");
        cmd.Parameters.AddWithValue(commentId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new KnownComment(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2))
            : null;
    }

    public async Task<bool> AddLikeAsync(LikeTarget target, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        if (!await TargetIsLiveAsync(connection, tx, target, cancellationToken))
        {
            return false;
        }

        await using (var insert = new NpgsqlCommand("""
            insert into likes (target_type, target_id, user_id, video_id, created_at) values ($1, $2, $3, $4, now())
            on conflict do nothing
            """, connection, tx))
        {
            insert.Parameters.AddWithValue(TypeName(target.Type));
            insert.Parameters.AddWithValue(target.Id);
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(target.VideoId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return false;
            }
        }

        await using (var count = new NpgsqlCommand("""
            insert into like_counts (target_type, target_id, count) values ($1, $2, 1)
            on conflict (target_type, target_id) do update set count = like_counts.count + 1
            """, connection, tx))
        {
            count.Parameters.AddWithValue(TypeName(target.Type));
            count.Parameters.AddWithValue(target.Id);
            await count.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnqueueAsync(connection, tx, target, userId, liked: true, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveLikeAsync(LikeTarget target, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = new NpgsqlCommand(
            "delete from likes where target_type = $1 and target_id = $2 and user_id = $3", connection, tx))
        {
            delete.Parameters.AddWithValue(TypeName(target.Type));
            delete.Parameters.AddWithValue(target.Id);
            delete.Parameters.AddWithValue(userId);
            if (await delete.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return false;
            }
        }

        await using (var count = new NpgsqlCommand(
            "update like_counts set count = greatest(count - 1, 0) where target_type = $1 and target_id = $2", connection, tx))
        {
            count.Parameters.AddWithValue(TypeName(target.Type));
            count.Parameters.AddWithValue(target.Id);
            await count.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnqueueAsync(connection, tx, target, userId, liked: false, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<LikeCount>> GetCountsAsync(
        LikeTargetType type, IReadOnlyCollection<Guid> ids, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await using var cmd = dataSource.CreateCommand("""
            select i.id, coalesce(c.count, 0),
                   exists (select 1 from likes l where l.target_type = $1 and l.target_id = i.id and l.user_id = $3)
              from unnest($2::uuid[]) with ordinality as i(id, ord)
              left join like_counts c on c.target_type = $1 and c.target_id = i.id
             order by i.ord
            """);
        cmd.Parameters.AddWithValue(TypeName(type));
        cmd.Parameters.AddWithValue(ids.ToArray());
        cmd.Parameters.AddWithValue(userId);
        var result = new List<LikeCount>(ids.Count);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new LikeCount(reader.GetGuid(0), reader.GetInt32(1), reader.GetBoolean(2)));
        }

        return result;
    }

    public async Task<IReadOnlyList<Liker>> ListLikersAsync(Guid videoId, LikerCursor? after, int limit, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            select user_id, created_at
              from likes
             where target_type = 'video' and target_id = $1
               and ($2::timestamptz is null or (created_at, user_id) < ($2, $3))
             order by created_at desc, user_id desc
             limit $4
            """);
        cmd.Parameters.AddWithValue(videoId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = after is null ? DBNull.Value : after.LikedAt.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = after?.UserId ?? string.Empty, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(limit);
        var result = new List<Liker>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Liker(reader.GetString(0), new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))));
        }

        return result;
    }

    public async Task<int> CountVideoLikesAsync(Guid videoId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("select count from like_counts where target_type = 'video' and target_id = $1");
        cmd.Parameters.AddWithValue(videoId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is int n ? n : 0;
    }

    private static string TypeName(LikeTargetType type) => type == LikeTargetType.Video ? "video" : "comment";

    /// <summary>Row lock "for share": a concurrent deletion of the video or comment (which writes the tombstone) waits for this transaction.</summary>
    private static async Task<bool> TargetIsLiveAsync(NpgsqlConnection connection, NpgsqlTransaction tx, LikeTarget target, CancellationToken cancellationToken)
    {
        var sql = target.Type == LikeTargetType.Video
            ? "select 1 from known_videos where video_id = $1 and deleted_at is null for share"
            : "select 1 from known_comments where comment_id = $1 and deleted_at is null for share";
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue(target.Id);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static Task EnqueueAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, LikeTarget target, string userId, bool liked, CancellationToken cancellationToken)
    {
        var e = LikeEvents.Build(target, userId, liked, TimeProvider.System.GetUtcNow(), Activity.Current?.Id);
        return OutboxStore.EnqueueAsync(connection, tx, e.EventId, e.Topic, e.PayloadJson, LikeEvents.Subscribers.ToArray(), cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
