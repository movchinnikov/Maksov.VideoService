using System.Text.Json;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Videos.Features.Videos;

public sealed record VideoDto(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long? SizeBytes,
    int? DurationMs,
    int? Width,
    int? Height,
    IReadOnlyList<RenditionDto>? Renditions,
    string? FailureReason,
    PlaybackDto? Playback,
    string OwnerId,
    string OwnerUsername)
{
    internal static VideoDto From(VideoRecord v)
    {
        var renditions = v.RenditionsJson is null
            ? null
            : JsonSerializer.Deserialize(v.RenditionsJson, VideosJsonContext.Default.ListRenditionDto);

        var playback = v.Status == VideoStatus.Ready
            ? new PlaybackDto(
                Dash: $"/api/videos/{v.Id}/stream/{MediaKeys.DashManifest}",
                Hls: $"/api/videos/{v.Id}/stream/{MediaKeys.HlsMaster}",
                Poster: $"/api/videos/{v.Id}/poster")
            : null;

        return new VideoDto(v.Id, v.Title, v.Status.ToWire(), v.CreatedAt, v.UpdatedAt, v.SizeBytes, v.DurationMs,
            v.Width, v.Height, renditions, v.FailureReason, playback, v.OwnerId, v.OwnerUsername);
    }
}

public sealed record RenditionDto(string Name, int Width, int Height, int Kbps);

public sealed record PlaybackDto(string Dash, string Hls, string Poster);

