using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Feed.Bll.Ports;

namespace Maksov.VideoService.Feed.Dal.Http;

internal sealed record ReadyByOwnersRequestBody(IReadOnlyCollection<string> OwnerIds, string? Cursor, int Limit);

internal sealed record VideoIdBody(Guid Id);

internal sealed record ReadyByOwnersResponseBody(IReadOnlyList<VideoIdBody>? Items, string? NextCursor);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReadyByOwnersRequestBody))]
[JsonSerializable(typeof(ReadyByOwnersResponseBody))]
internal sealed partial class VideosClientJsonContext : JsonSerializerContext;

/// <summary>The videos service internal query "ready videos by owners" (ids only).</summary>
internal sealed class VideosCatalog(HttpClient http, string? internalKey) : IVideoCatalog
{
    public async Task<VideoIdPage> ReadyByOwnersAsync(IReadOnlyCollection<string> ownerIds, string? cursor, int limit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/videos/ready-by-owners")
        {
            Content = JsonContent.Create(new ReadyByOwnersRequestBody(ownerIds, cursor, limit), VideosClientJsonContext.Default.ReadyByOwnersRequestBody),
        };
        if (!string.IsNullOrEmpty(internalKey))
        {
            request.Headers.Add("X-Internal-Key", internalKey);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync(VideosClientJsonContext.Default.ReadyByOwnersResponseBody, cancellationToken).ConfigureAwait(false);
        return new VideoIdPage((body?.Items ?? []).Select(static i => i.Id).ToList(), body?.NextCursor);
    }
}
