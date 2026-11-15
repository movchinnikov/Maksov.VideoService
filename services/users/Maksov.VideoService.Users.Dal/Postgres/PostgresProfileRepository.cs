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

    public async Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition("select exists (select 1 from profiles where user_id = @userId)", new { userId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ProfileSummary>> GetManyAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return [];
        }

        const string sql = """
            select user_id as UserId, username as Username, display_name as DisplayName, bio as Bio
            from profiles where user_id = any(@ids) order by username
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(sql, new { ids = userIds.ToArray() }, cancellationToken: cancellationToken));
        return rows.Select(static r => new ProfileSummary(r.UserId, r.Username, r.DisplayName, r.Bio)).ToList();
    }

    public async Task<IReadOnlyList<ProfileSummary>> SearchAsync(string query, string viewerId, int limit, CancellationToken cancellationToken)
    {
        var pattern = EscapeLike(query.Trim().ToLowerInvariant()) + "%";
        const string sql = """
            select p.user_id as UserId, p.username as Username, p.display_name as DisplayName, p.bio as Bio
            from profiles p
            where p.user_id <> @viewerId
              and (lower(p.username) like @pattern escape '\' or lower(p.display_name) like @pattern escape '\')
            order by p.username
            limit @limit
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SummaryRow>(new CommandDefinition(sql, new { viewerId, pattern, limit }, cancellationToken: cancellationToken));
        return rows.Select(static r => new ProfileSummary(r.UserId, r.Username, r.DisplayName, r.Bio)).ToList();
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

    private sealed class SummaryRow
    {
        public string UserId { get; init; } = "";
        public string Username { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Bio { get; init; } = "";
    }
}
