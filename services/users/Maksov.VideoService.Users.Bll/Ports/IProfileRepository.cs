namespace Maksov.VideoService.Users.Bll.Ports;

/// <summary>Port for storing profiles. Identity and passwords live in Keycloak; here is what a user says about themselves. Follows live in the friends service.</summary>
public interface IProfileRepository
{
    /// <summary>Profile by Keycloak identifier (sub); created on first access. The login is synchronized.</summary>
    Task<ProfileRecord> GetOrCreateAsync(string userId, string username, CancellationToken cancellationToken);

    Task<ProfileRecord?> GetAsync(string userId, CancellationToken cancellationToken);

    Task<ProfileRecord?> UpdateAsync(string userId, string displayName, string bio, CancellationToken cancellationToken);

    /// <summary>Whether a profile exists (used by the friends service before following).</summary>
    Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Profiles by identifiers (batch lookup); unknown ids are skipped, order is by login.</summary>
    Task<IReadOnlyList<ProfileSummary>> GetManyAsync(IReadOnlyCollection<string> userIds, CancellationToken cancellationToken);

    /// <summary>Search people by login or name prefix (excluding the viewer), ordered by login.</summary>
    Task<IReadOnlyList<ProfileSummary>> SearchAsync(string query, string viewerId, int limit, CancellationToken cancellationToken);
}

public sealed record ProfileRecord(string UserId, string Username, string DisplayName, string Bio, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ProfileSummary(string UserId, string Username, string DisplayName, string Bio);
