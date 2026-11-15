using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Videos.Bll.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.Dal.Http;

public sealed class UsersClientOptions
{
    public const string SectionName = "Users";

    /// <summary>Address of the users service on the internal network (e.g. http://users:8080).</summary>
    public string BaseUrl { get; set; } = "";

    public string InternalApiKey { get; set; } = "";
}

internal sealed record FollowingIdsResponse(IReadOnlyList<string> Ids);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FollowingIdsResponse))]
internal sealed partial class UsersClientJsonContext : JsonSerializerContext;

/// <summary>
/// Adapter to the users service: whom the user follows. Short cache: the feed is opened often, follows change rarely.
/// Unavailability of users does not break the feed: we show only the user's own videos and log a warning.
/// </summary>
internal sealed partial class UsersFollowingProvider(
    HttpClient http,
    IMemoryCache cache,
    IOptions<UsersClientOptions> options,
    ILogger<UsersFollowingProvider> logger) : IFollowingProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var cacheKey = ("following", userId);
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/internal/v1/users/{Uri.EscapeDataString(userId)}/following-ids", UriKind.Relative));
            request.Headers.Add("X-Internal-Key", options.Value.InternalApiKey);
            using var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync(UsersClientJsonContext.Default.FollowingIdsResponse, cancellationToken);
            IReadOnlyList<string> ids = body?.Ids ?? [];
            cache.Set(cacheKey, ids, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl, Size = 1 });
            return ids;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUsersUnavailable(logger, ex);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Users service unavailable, feed shows only own videos")]
    private static partial void LogUsersUnavailable(ILogger logger, Exception ex);
}
