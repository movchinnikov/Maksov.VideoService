namespace Maksov.VideoService.Friends.Bll.Ports;

public sealed record FriendStats(int FollowersCount, int FollowingCount, bool IsFollowing, bool FollowsYou)
{
    /// <summary>Both follow each other.</summary>
    public bool IsMutual => IsFollowing && FollowsYou;
}

/// <summary>A row of a followers/following list with the relation to the viewer.</summary>
public sealed record RelationRow(string UserId, DateTimeOffset FollowedAt, bool IsFollowing, bool FollowsYou);

/// <summary>Keyset position in a followers/following list (follow time, user id), newest first.</summary>
public sealed record RelationCursor(DateTimeOffset FollowedAt, string UserId)
{
    public string Encode() => System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes($"{FollowedAt.UtcTicks}:{UserId}"));

    public static RelationCursor? TryDecode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512)
        {
            return null;
        }

        try
        {
            var text = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(value));
            var split = text.IndexOf(':', StringComparison.Ordinal);
            if (split <= 0 || !long.TryParse(text.AsSpan(0, split), out var ticks) || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return null;
            }

            var id = text[(split + 1)..];
            return id.Length == 0 ? null : new RelationCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>A suggested user: friend of a friend, ranked by cosine similarity of graph embeddings.</summary>
public sealed record Recommendation(string UserId, int MutualFriends, double Similarity);

/// <summary>Port of the social graph storage (follows + user embeddings). Adapter: Postgres with pgvector.</summary>
public interface IFriendsRepository
{
    /// <summary>Follow. Idempotent. Also refreshes the follower's embedding and makes sure the followee has one.</summary>
    Task FollowAsync(string followerId, string followeeId, CancellationToken cancellationToken);

    /// <summary>Unfollow. Idempotent. Refreshes the follower's embedding.</summary>
    Task UnfollowAsync(string followerId, string followeeId, CancellationToken cancellationToken);

    Task<FriendStats> GetStatsAsync(string userId, string viewerId, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="candidateIds"/> the viewer follows.</summary>
    Task<IReadOnlyList<string>> GetFollowedAmongAsync(string viewerId, IReadOnlyCollection<string> candidateIds, CancellationToken cancellationToken);

    /// <summary>Which of <paramref name="candidateIds"/> follow the viewer.</summary>
    Task<IReadOnlyList<string>> GetFollowedByAmongAsync(string viewerId, IReadOnlyCollection<string> candidateIds, CancellationToken cancellationToken);

    /// <summary>People who follow <paramref name="userId"/>, newest first, with the relation to the viewer.</summary>
    Task<IReadOnlyList<RelationRow>> ListFollowersAsync(string userId, string viewerId, RelationCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>People <paramref name="userId"/> follows, newest first, with the relation to the viewer.</summary>
    Task<IReadOnlyList<RelationRow>> ListFollowingAsync(string userId, string viewerId, RelationCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>Identifiers of the users the user follows, newest follow first.</summary>
    Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, int limit, CancellationToken cancellationToken);

    /// <summary>Friends of the viewer's friends the viewer does not follow, most similar first.</summary>
    Task<IReadOnlyList<Recommendation>> RecommendAsync(string userId, int limit, CancellationToken cancellationToken);
}

/// <summary>Port to the users service: does the profile exist.</summary>
public interface IProfileDirectory
{
    /// <summary>Throws <see cref="HttpRequestException"/> if the users service is unavailable.</summary>
    Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken);
}
