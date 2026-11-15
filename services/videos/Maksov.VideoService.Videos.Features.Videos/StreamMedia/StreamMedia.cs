using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Web;
using Microsoft.Extensions.Caching.Memory;

namespace Maksov.VideoService.Videos.Features.Videos.StreamMedia;

/// <summary>
/// Serves DASH/HLS manifests, CMAF segments and the poster of a ready video to any authenticated user
/// (videos are public to registered users), streamed from MinIO without buffering.
/// The "ready and not deleted" check is cached for 30 s: the player requests a segment every few seconds.
/// </summary>
internal sealed class StreamMediaHandler(IVideoRepository videos, IObjectStorage storage, IMemoryCache cache)
{
    private static readonly TimeSpan ReadyTtl = TimeSpan.FromSeconds(30);

    public async Task<StoredObject?> OpenAsync(string viewerId, Guid videoId, string key, CancellationToken ct)
    {
        if (!await IsReadyAsync(viewerId, videoId, ct))
        {
            return null;
        }
        return await storage.OpenReadAsync(key, ct);
    }

    /// <summary>Evicts the cache on deletion so the video disappears immediately rather than after the TTL.</summary>
    public void Forget(Guid videoId) => cache.Remove(videoId);

    private async Task<bool> IsReadyAsync(string viewerId, Guid videoId, CancellationToken ct)
    {
        if (cache.TryGetValue(videoId, out bool cached))
        {
            return cached;
        }

        var video = await videos.GetVisibleAsync(videoId, viewerId, ct);
        var ready = video?.Status == VideoStatus.Ready;
        if (ready)
        {
            // Only a positive answer is cached; for a ready video it does not depend on the viewer.
            cache.Set(videoId, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ReadyTtl, Size = 1 });
        }
        return ready;
    }
}

internal static class StreamMediaEndpoint
{
    public static void MapStreamMedia(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:guid}/stream/{file}", (Guid id, string file, HttpContext http, StreamMediaHandler handler, CancellationToken ct) =>
                !MediaKeys.IsValidStreamFile(file)
                    ? Task.FromResult<IResult>(TypedResults.NotFound())
                    : ServeAsync(http, handler, id, MediaKeys.StreamFile(id, file), MediaKeys.ContentTypeFor(file)!, MediaKeys.IsManifest(file), ct))
            .WithName("StreamMedia");

        group.MapGet("/{id:guid}/poster", (Guid id, HttpContext http, StreamMediaHandler handler, CancellationToken ct) =>
                ServeAsync(http, handler, id, MediaKeys.Poster(id), "image/jpeg", isManifest: false, ct))
            .WithName("GetPoster");
    }

    private static async Task<IResult> ServeAsync(
        HttpContext http, StreamMediaHandler handler, Guid id, string key, string contentType, bool isManifest, CancellationToken ct)
    {
        var obj = await handler.OpenAsync(http.User.UserId(), id, key, ct);
        if (obj is null)
        {
            return TypedResults.NotFound();
        }

        http.Response.RegisterForDisposeAsync(obj);
        http.Response.ContentLength = obj.Length;
        // Files do not change after Ready: the browser caches segments for a long time and manifests briefly.
        http.Response.Headers.CacheControl = isManifest ? "private, max-age=60" : "private, max-age=86400, immutable";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Stream(obj.Content, contentType);
    }
}
