using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Videos.Features.Feed;

public sealed record FeedPlaybackDto(string Dash, string Hls, string Poster);

/// <summary>Video card in the feed and in the profile grid.</summary>
public sealed record FeedItemDto(
    Guid Id,
    string Title,
    string OwnerId,
    string OwnerUsername,
    DateTimeOffset CreatedAt,
    int DurationMs,
    int Width,
    int Height,
    FeedPlaybackDto Playback)
{
    internal static FeedItemDto From(VideoRecord v) => new(
        v.Id, v.Title, v.OwnerId, v.OwnerUsername, v.CreatedAt, v.DurationMs ?? 0, v.Width ?? 0, v.Height ?? 0,
        new FeedPlaybackDto(
            Dash: $"/api/videos/{v.Id}/stream/{MediaKeys.DashManifest}",
            Hls: $"/api/videos/{v.Id}/stream/{MediaKeys.HlsMaster}",
            Poster: $"/api/videos/{v.Id}/poster"));
}

public sealed record FeedPage(IReadOnlyList<FeedItemDto> Items, string? NextCursor);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FeedPage))]
[JsonSerializable(typeof(FeedItemDto))]
internal sealed partial class FeedJsonContext : JsonSerializerContext;

internal static class Paging
{
    public const int DefaultLimit = 12;
    public const int MaxLimit = 30;

    /// <summary>Fetch one extra record to tell whether there is a next page, without COUNT(*).</summary>
    public static FeedPage ToPage(IReadOnlyList<VideoRecord> rows, int take)
    {
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var next = hasMore ? new PageCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return new FeedPage(page.Select(FeedItemDto.From).ToList(), next);
    }

    public static int Take(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}

/// <summary>
/// GetFeed slice: ready videos of the people the user follows plus their own, newest first.
/// Follows come from the users service (IFollowingProvider port); unavailability of users does not break the feed.
/// </summary>
internal sealed class GetFeedHandler(IVideoRepository videos, IFollowingProvider following)
{
    private const int MaxOwners = 2000;

    public async Task<FeedPage> HandleAsync(string viewerId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = Paging.Take(limit);
        var followed = await following.GetFollowingIdsAsync(viewerId, ct);
        var owners = followed.Append(viewerId).Distinct(StringComparer.Ordinal).Take(MaxOwners).ToArray();
        var rows = await videos.ListReadyByOwnersAsync(owners, PageCursor.TryDecode(cursor), take + 1, ct);
        return Paging.ToPage(rows, take);
    }
}

/// <summary>ListUserVideos slice: a person's ready videos (profile grid), public to authenticated users.</summary>
internal sealed class ListUserVideosHandler(IVideoRepository videos)
{
    public async Task<FeedPage> HandleAsync(string userId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = Paging.Take(limit);
        var rows = await videos.ListReadyByOwnersAsync([userId], PageCursor.TryDecode(cursor), take + 1, ct);
        return Paging.ToPage(rows, take);
    }
}

/// <summary>Registration and routes of the Feed feature.</summary>
public static class FeedFeatures
{
    public static IServiceCollection AddFeedFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, FeedJsonContext.Default));
        services.AddSingleton<GetFeedHandler>();
        services.AddSingleton<ListUserVideosHandler>();
        return services;
    }

    /// <param name="api">Group <c>/api</c> with mandatory authorization.</param>
    public static RouteGroupBuilder MapFeedFeatures(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/feed", async (string? cursor, int? limit, HttpContext http, GetFeedHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(http.User.UserId(), cursor, limit, ct)))
            .WithTags("Feed")
            .WithName("GetFeed");

        api.MapGet("/users/{id}/videos", async (string id, string? cursor, int? limit, ListUserVideosHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(id, cursor, limit, ct)))
            .WithTags("Feed")
            .WithName("ListUserVideos");

        return api;
    }
}
