using System.Diagnostics;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Web;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.Features.Videos.CompleteUpload;

/// <summary>
/// The client reports that it uploaded the file. We check the object in storage and enqueue the transcode job.
/// Idempotent: a repeated call returns the current state.
/// </summary>
internal sealed class CompleteUploadHandler(IVideoRepository videos, IObjectStorage storage, IOptions<UploadOptions> options)
{
    public async Task<Result<VideoDto>> HandleAsync(string ownerId, Guid videoId, CancellationToken ct)
    {
        var video = await videos.GetOwnedAsync(videoId, ownerId, ct);
        if (video is null)
        {
            return DomainError.NotFound();
        }

        if (video.Status.IsUploadCompleted())
        {
            return VideoDto.From(video);
        }

        if (video.Status == VideoStatus.Failed)
        {
            return DomainError.Conflict("video_failed", "The video has already been rejected. Upload the file again.");
        }

        var head = await storage.HeadAsync(video.SourceKey, ct);
        if (head is null)
        {
            return DomainError.Conflict("upload_missing", "The file has not been uploaded to storage yet.");
        }

        if (head.Length <= 0 || head.Length > options.Value.MaxBytes)
        {
            await storage.DeleteAsync(video.SourceKey, ct);
            await videos.MarkFailedAsync(video.Id, "file_too_large", ct);
            return DomainError.Validation("sizeBytes", "The file size exceeds the limit.");
        }

        await videos.MarkUploadedAndEnqueueAsync(video.Id, head.Length, Activity.Current?.Id, ct);
        var updated = await videos.GetOwnedAsync(videoId, ownerId, ct);
        return VideoDto.From(updated!);
    }
}

internal static class CompleteUploadEndpoint
{
    public static void MapCompleteUpload(this RouteGroupBuilder group) =>
        group.MapPost("/{id:guid}/complete", async (Guid id, HttpContext http, CompleteUploadHandler handler, CancellationToken ct) =>
            {
                var result = await handler.HandleAsync(http.User.UserId(), id, ct);
                return result.IsSuccess
                    ? TypedResults.Accepted($"/api/videos/{id}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CompleteUpload");
}
