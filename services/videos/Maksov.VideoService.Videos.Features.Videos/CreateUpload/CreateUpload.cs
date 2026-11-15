using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Web;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.Features.Videos.CreateUpload;

public sealed record CreateUploadRequest(string? Title, string? FileName, string? ContentType, long SizeBytes);

public sealed record CreateUploadResponse(
    Guid VideoId,
    Uri UploadUrl,
    string UploadMethod,
    IReadOnlyDictionary<string, string> UploadHeaders,
    DateTimeOffset ExpiresAt);

/// <summary>Creates a video record and issues a presigned PUT to MinIO. The file goes straight to storage, bypassing the API.</summary>
internal sealed class CreateUploadHandler(
    IVideoRepository videos,
    IObjectStorage storage,
    IOptions<UploadOptions> options,
    TimeProvider clock)
{
    private readonly UploadPolicy _policy = new(options.Value.MaxBytes, options.Value.AllowedContentTypes);
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(options.Value.UrlTtlMinutes);

    public async Task<Result<CreateUploadResponse>> HandleAsync(string ownerId, string ownerUsername, CreateUploadRequest request, CancellationToken ct)
    {
        var errors = _policy.Validate(request.Title, request.ContentType, request.SizeBytes);
        if (errors.Count > 0)
        {
            return errors[0];
        }

        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var contentType = request.ContentType!.ToLowerInvariant();
        var key = MediaKeys.Source(id);

        await videos.InsertAsync(new NewVideo(id, ownerId, request.Title!.Trim(), key, contentType, request.SizeBytes, now, ownerUsername), ct);
        var url = await storage.PresignUploadAsync(key, contentType, _ttl);

        return new CreateUploadResponse(
            id,
            url,
            "PUT",
            new Dictionary<string, string> { ["Content-Type"] = contentType },
            now.Add(_ttl));
    }

    internal IReadOnlyList<DomainError> Validate(CreateUploadRequest request) =>
        _policy.Validate(request.Title, request.ContentType, request.SizeBytes);
}

internal static class CreateUploadEndpoint
{
    public static void MapCreateUpload(this RouteGroupBuilder group) =>
        group.MapPost("/", async (CreateUploadRequest request, HttpContext http, CreateUploadHandler handler, CancellationToken ct) =>
            {
                var errors = handler.Validate(request);
                if (errors.Count > 0)
                {
                    return errors.ToValidationProblem();
                }

                var result = await handler.HandleAsync(http.User.UserId(), http.User.Username(), request, ct);
                return result.IsSuccess
                    ? TypedResults.Created($"/api/videos/{result.Value!.VideoId}", result.Value)
                    : result.Error!.ToProblem();
            })
            .WithName("CreateUpload");
}
