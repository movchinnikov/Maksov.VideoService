using System.ComponentModel.DataAnnotations;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.Features.Videos.CompleteUpload;
using Maksov.VideoService.Videos.Features.Videos.CreateUpload;
using Maksov.VideoService.Videos.Features.Videos.DeleteVideo;
using Maksov.VideoService.Videos.Features.Videos.GetVideo;
using Maksov.VideoService.Videos.Features.Videos.ListMyVideos;
using Maksov.VideoService.Videos.Features.Videos.StreamMedia;

namespace Maksov.VideoService.Videos.Features.Videos;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    [Range(1, long.MaxValue)]
    public long MaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    [Range(1, 120)]
    public int UrlTtlMinutes { get; set; } = 15;

    public string[] AllowedContentTypes { get; set; } = [.. UploadPolicy.DefaultContentTypes];
}

/// <summary>Registration and routes of the Videos area. Each slice owns its route and handler.</summary>
public static class VideoFeatures
{
    public static IServiceCollection AddVideoFeatures(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UploadOptions>()
            .Bind(configuration.GetSection(UploadOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, VideosJsonContext.Default));

        // Handlers are stateless singletons: zero DI allocations per request.
        services.AddSingleton<CreateUploadHandler>();
        services.AddSingleton<CompleteUploadHandler>();
        services.AddSingleton<ListMyVideosHandler>();
        services.AddSingleton<GetVideoHandler>();
        services.AddSingleton<DeleteVideoHandler>();
        services.AddSingleton<StreamMediaHandler>();
        return services;
    }

    public static RouteGroupBuilder MapVideoFeatures(this RouteGroupBuilder api)
    {
        var videos = api.MapGroup("/videos").WithTags("Videos");
        videos.MapCreateUpload();
        videos.MapCompleteUpload();
        videos.MapListMyVideos();
        videos.MapGetVideo();
        videos.MapDeleteVideo();
        videos.MapStreamMedia();
        return api;
    }
}

