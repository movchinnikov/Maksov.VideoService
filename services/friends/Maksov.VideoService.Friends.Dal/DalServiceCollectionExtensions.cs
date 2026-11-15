using System.Reflection;
using Maksov.VideoService.Friends.Bll.Ports;
using Maksov.VideoService.Friends.Dal.Http;
using Maksov.VideoService.Friends.Dal.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Friends.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Assembly with embedded SQL migrations (for SqlMigrator).</summary>
    public static Assembly MigrationsAssembly => typeof(DalServiceCollectionExtensions).Assembly;

    public static IServiceCollection AddDal(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<IFriendsRepository, PostgresFriendsRepository>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);

        services.AddMemoryCache(static o => o.SizeLimit = 10_000);
        services.Configure<UsersClientOptions>(configuration.GetSection(UsersClientOptions.SectionName));
        var users = configuration.GetSection(UsersClientOptions.SectionName).Get<UsersClientOptions>() ?? new UsersClientOptions();
        services.AddHttpClient<IProfileDirectory, UsersProfileDirectory>(client =>
        {
            client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(users.BaseUrl) ? "http://users:8080" : users.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        });
        return services;
    }
}
