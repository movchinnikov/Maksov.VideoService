using Maksov.VideoService.Primitives;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Videos.Features.Videos.GetVideo;

internal sealed class GetVideoHandler(IVideoRepository videos)
{
    /// <summary>A ready video is visible to any authenticated user; a video in processing or failed is visible only to its owner.</summary>
    public async Task<Result<VideoDto>> HandleAsync(string viewerId, Guid id, CancellationToken ct)
    {
        var video = await videos.GetVisibleAsync(id, viewerId, ct);
        return video is null ? DomainError.NotFound() : VideoDto.From(video);
    }
}

internal static class GetVideoEndpoint
{
    public static void MapGetVideo(this RouteGroupBuilder group) =>
        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, GetVideoHandler handler, CancellationToken ct) =>
            {
                var result = await handler.HandleAsync(http.User.UserId(), id, ct);
                return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
            })
            .WithName("GetVideo");
}
