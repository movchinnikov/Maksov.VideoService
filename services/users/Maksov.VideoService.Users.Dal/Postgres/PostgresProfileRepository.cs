using Dapper;
using Maksov.VideoService.Users.Bll.Ports;
using Npgsql;

namespace Maksov.VideoService.Users.Dal.Postgres;

internal sealed class PostgresProfileRepository(NpgsqlDataSource dataSource) : IProfileRepository
{
    private const string Columns = "user_id as UserId, username as Username, display_name as DisplayName, bio as Bio, created_at as CreatedAt, updated_at as UpdatedAt";

    public async Task<ProfileRecord> GetOrCreateAsync(string userId, string username, CancellationToken cancellationToken)
    {
        // Atomic and idempotent: concurrent first requests do not create a duplicate.
        var sql = $"""
            insert into profiles (user_id, username, display_name) values (@userId, @username, left(@username, 60))
            on conflict (user_id) do update set username = excluded.username
            returning {Columns}
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<Row>(new CommandDefinition(sql, new { userId, username }, cancellationToken: cancellationToken));
        return row.ToRecord();
    }

    public async Task<ProfileRecord?> GetAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition($"select {Columns} from profiles where user_id = @userId", new { userId }, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<ProfileRecord?> UpdateAsync(string userId, string displayName, string bio, CancellationToken cancellationToken)
    {
        var sql = $"""
            update profiles set display_name = @displayName, bio = @bio, updated_at = now()
            where user_id = @userId
            returning {Columns}
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(sql, new { userId, displayName, bio }, cancellationToken: cancellationToken));
        return row?.ToRecord();
    }

    public async Task<ProfileView?> GetViewAsync(string userId, string viewerId, CancellationToken cancellationToken)
    {
        var sql = $"""
            select {Columns},
                   (select count(*) from follows where followee_id = p.user_id)::int as FollowersCount,
                   (select count(*) from follows where follower_id = p.user_id)::int as FollowingCount,
                   exists(select 1 from follows where follower_id = @viewerId and followee_id = p.user_id) as IsFollowing
            from profiles p
            where p.user_id = @userId
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ViewRow>(
            new CommandDefinition(sql, new { userId, viewerId }, cancellationToken: cancellationToken));
        return row is null ? null : new ProfileView(row.ToRecord(), row.FollowersCount, row.FollowingCount, row.IsFollowing);
    }

    public async Task<IReadOnlyList<ProfileSummary>> SearchAsync(string query, string viewerId, int limit, CancellationToken cancellationToken)
    {
        // Escape LIKE wildcards in the query: "50%" is matched literally.
        var pattern = EscapeLike(query.Trim().ToLowerInvariant()) + "%";
        const string sql = """
            select p.user_id as UserId, p.username as Username, p.display_name as DisplayName, p.bio as Bio,
                   exists(select 1 from follows f where f.follower_id = @viewerId and f.followee_id = p.user_id) as IsFollowing
            from profiles p
            where p.user_id <> @viewerId
              and (lower(p.username) like @pattern escape '\' or lower(p.display_name) like @pattern escape '\')
            order by p.username
            limit @limit
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(sql, new { viewerId, pattern, limit }, cancellationToken: cancellationToken));
        return rows.Select(static r => new ProfileSummary(r.UserId, r.Username, r.DisplayName, r.Bio, r.IsFollowing)).ToList();
    }

    public async Task<bool> FollowAsync(string followerId, string followeeId, CancellationToken cancellationToken)
    {
        // Following a non-existent user creates nothing; a repeat is a no-op.
        const string sql = """
            with target as (select 1 from profiles where user_id = @followeeId),
                 ins as (
                     insert into follows (follower_id, followee_id)
                     select @followerId, @followeeId where exists (select 1 from target)
                     on conflict do nothing
                 )
            select exists (select 1 from target)
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { followerId, followeeId }, cancellationToken: cancellationToken));
    }

    public async Task UnfollowAsync(string followerId, string followeeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from follows where follower_id = @followerId and followee_id = @followeeId", new { followerId, followeeId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<string>(new CommandDefinition(
            "select followee_id from follows where follower_id = @userId order by created_at desc limit @limit", new { userId, limit }, cancellationToken: cancellationToken));
        return ids.ToList();
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private class Row
    {
        public string UserId { get; init; } = "";
        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Bio { get; init; } = "";
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }

        public ProfileRecord ToRecord() => new(
            UserId, Username, DisplayName, Bio,
            new DateTimeOffset(DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc)));
    }

    private sealed class ViewRow : Row
    {
        public int FollowersCount { get; init; }
        public int FollowingCount { get; init; }
        public bool IsFollowing { get; init; }
    }

    private sealed class SummaryRow
    {
        public string UserId { get; init; } = "";
        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Bio { get; init; } = "";
        public bool IsFollowing { get; init; }
    }
}
