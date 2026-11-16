using System.Reflection;
using Maksov.VideoService.Likes.Bll.Ports;
using Maksov.VideoService.Likes.Dal.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Likes.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Assembly with embedded SQL migrations (for SqlMigrator).</summary>
    public static Assembly MigrationsAssembly => typeof(DalServiceCollectionExtensions).Assembly;

    public static IServiceCollection AddDal(this IServiceCollection services)
    {
        services.AddSingleton<ILikeRepository, PostgresLikeRepository>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }
}
