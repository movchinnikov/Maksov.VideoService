using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Feed.Bll.Ports;
using Microsoft.Extensions.Caching.Memory;

namespace Maksov.VideoService.Feed.Dal.Http;

public sealed class InternalClientOptions
{
    public const string FriendsSection = "Friends";
    public const string VideosSection = "Videos";

    /// <summary>Base address of the service, e.g. <c>http://friends:8080</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Shared key for internal calls (<c>X-Internal-Key</c>).</summary>
    public string? InternalApiKey { get; set; }
}

internal sealed record FollowingIdsBody(IReadOnlyList<string>? Ids);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FollowingIdsBody))]
internal sealed partial class FriendsClientJsonContext : JsonSerializerContext;

/// <summary>Following ids from the friends service, cached for 20 s (only successful answers are cached).</summary>
internal sealed class FriendsFollowingSource(HttpClient http, IMemoryCache cache, string? internalKey) : IFollowingSource
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, CancellationToken cancellationToken)
    {
        var key = "following:" + userId;
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/friends/{Uri.EscapeDataString(userId)}/following-ids");
        if (!string.IsNullOrEmpty(internalKey))
        {
            request.Headers.Add("X-Internal-Key", internalKey);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync(FriendsClientJsonContext.Default.FollowingIdsBody, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> ids = body?.Ids ?? [];
        cache.Set(key, ids, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl, Size = 1 });
        return ids;
    }
}
