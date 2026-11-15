namespace Maksov.VideoService.Feed.Bll.Ports;

/// <summary>Port to the friends service: whom the user follows.</summary>
public interface IFollowingSource
{
    /// <summary>Identifiers of the followed users. Throws <see cref="HttpRequestException"/> if the friends service is unavailable.</summary>
    Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, CancellationToken cancellationToken);
}

public sealed record VideoIdPage(IReadOnlyList<Guid> Ids, string? NextCursor);

/// <summary>Port to the videos service: the newest ready videos of a set of owners (ids only).</summary>
public interface IVideoCatalog
{
    /// <summary>Throws <see cref="HttpRequestException"/> if the videos service is unavailable.</summary>
    Task<VideoIdPage> ReadyByOwnersAsync(IReadOnlyCollection<string> ownerIds, string? cursor, int limit, CancellationToken cancellationToken);
}
