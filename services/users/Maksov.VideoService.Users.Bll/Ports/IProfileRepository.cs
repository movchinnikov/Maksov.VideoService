namespace Maksov.VideoService.Users.Bll.Ports;

/// <summary>Port for storing profiles and follows. Identity and passwords live in Keycloak; here is what a user says about themselves and whom they follow.</summary>
public interface IProfileRepository
{
    /// <summary>Profile by Keycloak identifier (sub); created on first access. The login is synchronized.</summary>
    Task<ProfileRecord> GetOrCreateAsync(string userId, string username, CancellationToken cancellationToken);

    Task<ProfileRecord?> GetAsync(string userId, CancellationToken cancellationToken);

    Task<ProfileRecord?> UpdateAsync(string userId, string displayName, string bio, CancellationToken cancellationToken);

    /// <summary>Profile with follower/following counters and the "viewer follows" flag.</summary>
    Task<ProfileView?> GetViewAsync(string userId, string viewerId, CancellationToken cancellationToken);

    /// <summary>Search people by login or name prefix (excluding the viewer), ordered by login.</summary>
    Task<IReadOnlyList<ProfileSummary>> SearchAsync(string query, string viewerId, int limit, CancellationToken cancellationToken);

    /// <summary>Follow. Idempotent. <c>false</c> means no such user.</summary>
    Task<bool> FollowAsync(string followerId, string followeeId, CancellationToken cancellationToken);

    Task UnfollowAsync(string followerId, string followeeId, CancellationToken cancellationToken);

    /// <summary>Identifiers of the users the user follows (for the feed).</summary>
    Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, int limit, CancellationToken cancellationToken);
}

public sealed record ProfileRecord(string UserId, string Username, string DisplayName, string Bio, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ProfileView(ProfileRecord Profile, int FollowersCount, int FollowingCount, bool IsFollowing);

public sealed record ProfileSummary(string UserId, string Username, string DisplayName, string Bio, bool IsFollowing);
