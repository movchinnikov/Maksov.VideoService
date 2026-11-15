using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Videos.Features.Videos.CreateUpload;
using Maksov.VideoService.Videos.Features.Videos.ListMyVideos;

namespace Maksov.VideoService.Videos.Features.Videos;

/// <summary>Source-generated serialization for the Videos feature: no reflection, fewer allocations, faster startup.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CreateUploadRequest))]
[JsonSerializable(typeof(CreateUploadResponse))]
[JsonSerializable(typeof(VideoDto))]
[JsonSerializable(typeof(VideoPage))]
[JsonSerializable(typeof(List<RenditionDto>))]
internal sealed partial class VideosJsonContext : JsonSerializerContext;
