using Dapper;
using Maksov.VideoService.Friends.Bll;
using Maksov.VideoService.Friends.Bll.Ports;
using Npgsql;
using NpgsqlTypes;

namespace Maksov.VideoService.Friends.Dal.Postgres;

internal sealed class PostgresFriendsRepository(NpgsqlDataSource dataSource) : IFriendsRepository
{
    public async Task FollowAsync(string followerId, string followeeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "insert into follows (follower_id, followee_id) values (@followerId, @followeeId) on conflict do nothing",
            new { followerId, followeeId }, tx, cancellationToken: cancellationToken));

        // The followee gets its own base embedding if it has none yet, so it can be recommended.
        await UpsertEmbeddingAsync(connection, tx, followeeId, GraphEmbedding.Base(followeeId), overwrite: false, cancellationToken);
        await RefreshEmbeddingAsync(connection, tx, followerId, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task UnfollowAsync(string followerId, string followeeId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "delete from follows where follower_id = @followerId and followee_id = @followeeId",
            new { followerId, followeeId }, tx, cancellationToken: cancellationToken));
        await RefreshEmbeddingAsync(connection, tx, followerId, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<FriendStats> GetStatsAsync(string userId, string viewerId, CancellationToken cancellationToken)
    {
        const string sql = """
            select (select count(*) from follows where followee_id = @userId)::int as FollowersCount,
                   (select count(*) from follows where follower_id = @userId)::int as FollowingCount,
                   exists (select 1 from follows where follower_id = @viewerId and followee_id = @userId) as IsFollowing,
                   exists (select 1 from follows where follower_id = @userId and followee_id = @viewerId) as FollowsYou
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<StatsRow>(new CommandDefinition(sql, new { userId, viewerId }, cancellationToken: cancellationToken));
        return new FriendStats(row.FollowersCount, row.FollowingCount, row.IsFollowing, row.FollowsYou);
    }

    public async Task<IReadOnlyList<string>> GetFollowedAmongAsync(string viewerId, IReadOnlyCollection<string> candidateIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (candidateIds.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<string>(new CommandDefinition(
            "select followee_id from follows where follower_id = @viewerId and followee_id = any(@ids)",
            new { viewerId, ids = candidateIds.ToArray() }, cancellationToken: cancellationToken));
        return ids.ToList();
    }

    public async Task<IReadOnlyList<string>> GetFollowedByAmongAsync(string viewerId, IReadOnlyCollection<string> candidateIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        if (candidateIds.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<string>(new CommandDefinition(
            "select follower_id from follows where followee_id = @viewerId and follower_id = any(@ids)",
            new { viewerId, ids = candidateIds.ToArray() }, cancellationToken: cancellationToken));
        return ids.ToList();
    }

    public Task<IReadOnlyList<RelationRow>> ListFollowersAsync(string userId, string viewerId, RelationCursor? after, int limit, CancellationToken cancellationToken) =>
        ListAsync(listFollowers: true, userId, viewerId, after, limit, cancellationToken);

    public Task<IReadOnlyList<RelationRow>> ListFollowingAsync(string userId, string viewerId, RelationCursor? after, int limit, CancellationToken cancellationToken) =>
        ListAsync(listFollowers: false, userId, viewerId, after, limit, cancellationToken);

    private async Task<IReadOnlyList<RelationRow>> ListAsync(
        bool listFollowers, string userId, string viewerId, RelationCursor? after, int limit, CancellationToken cancellationToken)
    {
        // "other" is the person shown in the row; the flags describe the relation between the viewer and that person.
        var other = listFollowers ? "f.follower_id" : "f.followee_id";
        var owner = listFollowers ? "f.followee_id" : "f.follower_id";
        var sql = $"""
            select {other} as UserId, f.created_at as FollowedAt,
                   exists (select 1 from follows v where v.follower_id = @viewerId and v.followee_id = {other}) as IsFollowing,
                   exists (select 1 from follows v where v.follower_id = {other} and v.followee_id = @viewerId) as FollowsYou
              from follows f
             where {owner} = @userId
               and (@hasCursor = false or (f.created_at, {other}) < (@createdAt, @cursorUserId))
             order by f.created_at desc, {other} desc
             limit @limit
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<RelationDbRow>(new CommandDefinition(sql, new
        {
            userId,
            viewerId,
            hasCursor = after is not null,
            createdAt = after?.FollowedAt.UtcDateTime ?? DateTime.UnixEpoch,
            cursorUserId = after?.UserId ?? string.Empty,
            limit,
        }, cancellationToken: cancellationToken));
        return rows.Select(static r => new RelationRow(r.UserId, new DateTimeOffset(DateTime.SpecifyKind(r.FollowedAt, DateTimeKind.Utc)), r.IsFollowing, r.FollowsYou)).ToList();
    }

    public async Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var ids = await connection.QueryAsync<string>(new CommandDefinition(
            "select followee_id from follows where follower_id = @userId order by created_at desc limit @limit",
            new { userId, limit }, cancellationToken: cancellationToken));
        return ids.ToList();
    }

    public async Task<IReadOnlyList<Recommendation>> RecommendAsync(string userId, int limit, CancellationToken cancellationToken)
    {
        // Candidates: people my friends follow, that I do not follow and that are not me. Ranked by the cosine distance
        // of their embedding to mine (pgvector operator <=>), then by the number of mutual friends.
        const string sql = """
            with mine as (select followee_id from follows where follower_id = $1),
                 candidates as (
                     select f.followee_id as candidate_id, count(distinct f.follower_id)::int as mutual
                       from follows f
                       join mine m on m.followee_id = f.follower_id
                      where f.followee_id <> $1
                        and f.followee_id not in (select followee_id from mine)
                      group by f.followee_id)
            select c.candidate_id, c.mutual, 1 - (e.embedding <=> me.embedding) as similarity
              from candidates c
              join user_embeddings e on e.user_id = c.candidate_id
              cross join (select embedding from user_embeddings where user_id = $1) me
             order by e.embedding <=> me.embedding, c.mutual desc, c.candidate_id
             limit $2
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(limit);
        var result = new List<Recommendation>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Recommendation(reader.GetString(0), reader.GetInt32(1), reader.GetDouble(2)));
        }

        return result;
    }

    private static async Task RefreshEmbeddingAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string userId, CancellationToken cancellationToken)
    {
        var following = (await connection.QueryAsync<string>(new CommandDefinition(
            "select followee_id from follows where follower_id = @userId", new { userId }, tx, cancellationToken: cancellationToken))).ToList();
        await UpsertEmbeddingAsync(connection, tx, userId, GraphEmbedding.Compute(userId, following), overwrite: true, cancellationToken);
    }

    private static async Task UpsertEmbeddingAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, string userId, float[] embedding, bool overwrite, CancellationToken cancellationToken)
    {
        var conflict = overwrite
            ? "do update set embedding = excluded.embedding, model_version = excluded.model_version, updated_at = now()"
            : "do nothing";
        await using var cmd = new NpgsqlCommand(
            $"insert into user_embeddings (user_id, embedding, model_version) values ($1, $2, $3) on conflict (user_id) {conflict}", connection, tx);
        cmd.Parameters.AddWithValue(userId);
        // Untyped text literal: the server parses it as vector(32).
        cmd.Parameters.Add(new NpgsqlParameter { Value = GraphEmbedding.ToVectorLiteral(embedding), NpgsqlDbType = NpgsqlDbType.Unknown });
        cmd.Parameters.AddWithValue(GraphEmbedding.ModelVersion);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class RelationDbRow
    {
        public string UserId { get; init; } = "";
        public DateTime FollowedAt { get; init; }
        public bool IsFollowing { get; init; }
        public bool FollowsYou { get; init; }
    }

    private sealed class StatsRow
    {
        public int FollowersCount { get; init; }
        public int FollowingCount { get; init; }
        public bool IsFollowing { get; init; }
        public bool FollowsYou { get; init; }
    }
}
