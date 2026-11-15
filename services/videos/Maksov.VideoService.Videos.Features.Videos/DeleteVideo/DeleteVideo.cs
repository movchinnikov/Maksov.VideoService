using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Features.Videos.StreamMedia;
using Maksov.VideoService.Web;
using Microsoft.Extensions.Logging;

namespace Maksov.VideoService.Videos.Features.Videos.DeleteVideo;

/// <summary>
/// Deletion of a video by its owner. In one transaction: status deleted, jobs cancelled and the <c>video.deleted.v1</c>
/// event is written (comments and description are removed by their services). Files in storage are removed immediately; if that fails, the video is still unavailable
/// (the DB status is the source of truth) and storage maintenance will pick up the leftovers.
/// </summary>
internal sealed partial class DeleteVideoHandler(
    IVideoRepository videos,
    IObjectStorage storage,
    StreamMediaHandler streaming,
    ILogger<DeleteVideoHandler> logger)
{
    public async Task<Result<bool>> HandleAsync(string ownerId, Guid videoId, CancellationToken ct)
    {
        if (!await videos.DeleteOwnedAsync(videoId, ownerId, ct))
        {
            return DomainError.NotFound();
        }

        streaming.Forget(videoId);
        try
        {
            await storage.DeletePrefixAsync(MediaKeys.VideoPrefix(videoId), ct);
            await storage.DeletePrefixAsync(MediaKeys.SourcePrefix(videoId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStorageCleanupFailed(logger, ex, videoId);
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Video {VideoId} deleted, but storage cleanup failed")]
    private static partial void LogStorageCleanupFailed(ILogger logger, Exception ex, Guid videoId);
}

internal static class DeleteVideoEndpoint
{
    public static void MapDeleteVideo(this RouteGroupBuilder group) =>
        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, DeleteVideoHandler handler, CancellationToken ct) =>
            {
                var result = await handler.HandleAsync(http.User.UserId(), id, ct);
                return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
            })
            .WithName("DeleteVideo");
}
