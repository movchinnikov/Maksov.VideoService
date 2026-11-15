using System.Reflection;
using Maksov.VideoService.Users.Bll.Ports;
using Maksov.VideoService.Users.Dal.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Users.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Assembly with embedded SQL migrations (for SqlMigrator).</summary>
    public static Assembly MigrationsAssembly => typeof(DalServiceCollectionExtensions).Assembly;

    public static IServiceCollection AddDal(this IServiceCollection services)
    {
        services.AddSingleton<IProfileRepository, PostgresProfileRepository>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }
}
