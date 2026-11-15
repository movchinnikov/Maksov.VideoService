using System.Reflection;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Dal.Http;
using Maksov.VideoService.Videos.Dal.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Videos.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Assembly with embedded SQL migrations (for SqlMigrator).</summary>
    public static Assembly MigrationsAssembly => typeof(DalServiceCollectionExtensions).Assembly;

    /// <summary>Registers the Bll port adapters: the Postgres video repository, the users service client and the DB readiness check.</summary>
    public static IServiceCollection AddDal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IVideoRepository, PostgresVideoRepository>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

        services.Configure<UsersClientOptions>(configuration.GetSection(UsersClientOptions.SectionName));
        var users = configuration.GetSection(UsersClientOptions.SectionName).Get<UsersClientOptions>() ?? new UsersClientOptions();
        if (!string.IsNullOrWhiteSpace(users.BaseUrl))
        {
            services.AddHttpClient<IFollowingProvider, UsersFollowingProvider>(client =>
            {
                client.BaseAddress = new Uri(users.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(3);
            });
        }
        else
        {
            services.AddSingleton<IFollowingProvider, NoFollowingProvider>();
        }

        return services;
    }

    /// <summary>The users service is not configured: the feed consists only of the user's own videos.</summary>
    private sealed class NoFollowingProvider : IFollowingProvider
    {
        public Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
