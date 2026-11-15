using Maksov.VideoService.Feed.Bll.Ports;
using Maksov.VideoService.Feed.Dal.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Feed.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Registers the HTTP clients of the friends and videos services behind the Bll ports.</summary>
    public static IServiceCollection AddDal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddMemoryCache(static o => o.SizeLimit = 10_000);

        var friends = configuration.GetSection(InternalClientOptions.FriendsSection).Get<InternalClientOptions>() ?? new InternalClientOptions();
        services.AddHttpClient(nameof(IFollowingSource), client =>
        {
            client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(friends.BaseUrl) ? "http://friends:8080" : friends.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        services.AddSingleton<IFollowingSource>(sp => new FriendsFollowingSource(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IFollowingSource)),
            sp.GetRequiredService<IMemoryCache>(),
            friends.InternalApiKey));

        var videos = configuration.GetSection(InternalClientOptions.VideosSection).Get<InternalClientOptions>() ?? new InternalClientOptions();
        services.AddHttpClient(nameof(IVideoCatalog), client =>
        {
            client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(videos.BaseUrl) ? "http://videos:8080" : videos.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<IVideoCatalog>(sp => new VideosCatalog(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IVideoCatalog)),
            videos.InternalApiKey));

        return services;
    }
}
