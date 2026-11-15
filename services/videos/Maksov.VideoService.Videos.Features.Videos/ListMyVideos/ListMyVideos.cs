using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Videos.Features.Videos.ListMyVideos;

public sealed record VideoPage(IReadOnlyList<VideoDto> Items, string? NextCursor);

internal sealed class ListMyVideosHandler(IVideoRepository videos)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public async Task<VideoPage> HandleAsync(string ownerId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var after = PageCursor.TryDecode(cursor);
        // Fetch one extra record to tell whether there is a next page, without COUNT(*).
        var rows = await videos.ListOwnedAsync(ownerId, after, take + 1, ct);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var next = hasMore ? new PageCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return new VideoPage(page.Select(VideoDto.From).ToList(), next);
    }
}

internal static class ListMyVideosEndpoint
{
    public static void MapListMyVideos(this RouteGroupBuilder group) =>
        group.MapGet("/", async (string? cursor, int? limit, HttpContext http, ListMyVideosHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(http.User.UserId(), cursor, limit, ct)))
            .WithName("ListMyVideos");
}

