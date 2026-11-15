using System.Reflection;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Dal.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Videos.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Assembly with embedded SQL migrations (for SqlMigrator).</summary>
    public static Assembly MigrationsAssembly => typeof(DalServiceCollectionExtensions).Assembly;

    /// <summary>Registers the Bll port adapters: the Postgres video repository and the DB readiness check.</summary>
    public static IServiceCollection AddDal(this IServiceCollection services)
    {
        services.AddSingleton<IVideoRepository, PostgresVideoRepository>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }
}
