using Maksov.VideoService.Friends.Bll.Ports;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Friends.Dal.Http;

public sealed class UsersClientOptions
{
    public const string SectionName = "Users";

    /// <summary>Base address of the users service, e.g. <c>http://users:8080</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Shared key for internal calls (<c>X-Internal-Key</c>).</summary>
    public string? InternalApiKey { get; set; }
}

/// <summary>Asks the users service whether a profile exists. Positive answers are cached for five minutes (profiles are not deleted).</summary>
internal sealed class UsersProfileDirectory(HttpClient http, IMemoryCache cache, IOptions<UsersClientOptions> options) : IProfileDirectory
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(5);

    public async Task<bool> ExistsAsync(string userId, CancellationToken cancellationToken)
    {
        var key = "profile-exists:" + userId;
        if (cache.TryGetValue(key, out _))
        {
            return true;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/users/{Uri.EscapeDataString(userId)}/exists");
        if (!string.IsNullOrEmpty(options.Value.InternalApiKey))
        {
            request.Headers.Add("X-Internal-Key", options.Value.InternalApiKey);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        cache.Set(key, true, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = PositiveTtl, Size = 1 });
        return true;
    }
}
