using GreenDonut;
using Maksov.VideoService.Gateway.GraphQL.Clients;

namespace Maksov.VideoService.Gateway.GraphQL.Schema;

// DataLoaders turn N field resolutions of one request into one downstream call per service (chunked at the service's id limit).

public sealed class UserByIdDataLoader(UsersClient users, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<string, Maybe<User>>(scheduler, options)
{
    private const int MaxIds = 50;

    protected override async Task<IReadOnlyDictionary<string, Maybe<User>>> LoadBatchAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, User>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(MaxIds))
        {
            foreach (var user in await users.GetManyAsync(chunk, cancellationToken))
            {
                found[user.Id] = user;
            }
        }

        return keys.ToDictionary(static k => k, k => found.TryGetValue(k, out var user) ? new Maybe<User>(user) : Maybe<User>.None, StringComparer.Ordinal);
    }
}

public sealed class VideoCardByIdDataLoader(VideosClient videos, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, Maybe<Video>>(scheduler, options)
{
    private const int MaxIds = 30;

    protected override async Task<IReadOnlyDictionary<Guid, Maybe<Video>>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, Video>();
        foreach (var chunk in keys.Chunk(MaxIds))
        {
            foreach (var card in await videos.GetCardsAsync(chunk, cancellationToken))
            {
                found[card.Id] = card;
            }
        }

        return keys.ToDictionary(static k => k, k => found.TryGetValue(k, out var card) ? new Maybe<Video>(card) : Maybe<Video>.None);
    }
}

public sealed class VideoLikesDataLoader(LikesClient likes, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, LikeInfo>(scheduler, options)
{
    private const int MaxIds = 50;

    protected override async Task<IReadOnlyDictionary<Guid, LikeInfo>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var items = new List<LikeItem>(keys.Count);
        foreach (var chunk in keys.Chunk(MaxIds))
        {
            items.AddRange(await likes.GetVideoLikesAsync(chunk, cancellationToken));
        }

        return LikeLookup.ToInfo(keys, items);
    }
}

public sealed class CommentLikesDataLoader(LikesClient likes, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<Guid, LikeInfo>(scheduler, options)
{
    private const int MaxIds = 50;

    protected override async Task<IReadOnlyDictionary<Guid, LikeInfo>> LoadBatchAsync(IReadOnlyList<Guid> keys, CancellationToken cancellationToken)
    {
        var items = new List<LikeItem>(keys.Count);
        foreach (var chunk in keys.Chunk(MaxIds))
        {
            items.AddRange(await likes.GetCommentLikesAsync(chunk, cancellationToken));
        }

        return LikeLookup.ToInfo(keys, items);
    }
}

/// <summary>The friends service has no batch endpoint yet: one request per distinct user, run in parallel, deduplicated by the loader.</summary>
public sealed class FriendStatsDataLoader(FriendsClient friends, IBatchScheduler scheduler, DataLoaderOptions options)
    : BatchDataLoader<string, FriendStats>(scheduler, options)
{
    protected override async Task<IReadOnlyDictionary<string, FriendStats>> LoadBatchAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var stats = await Task.WhenAll(keys.Select(k => friends.GetStatsAsync(k, cancellationToken)));
        var result = new Dictionary<string, FriendStats>(keys.Count, StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            result[keys[i]] = stats[i];
        }

        return result;
    }
}

internal static class LikeLookup
{
    public static IReadOnlyDictionary<Guid, LikeInfo> ToInfo(IReadOnlyList<Guid> keys, List<LikeItem> items)
    {
        var byId = items.ToDictionary(static i => i.Id, static i => new LikeInfo(i.Count, i.LikedByMe));
        return keys.ToDictionary(static k => k, k => byId.GetValueOrDefault(k) ?? new LikeInfo(0, false));
    }
}
