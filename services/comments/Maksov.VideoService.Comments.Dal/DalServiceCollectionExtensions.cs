using System.Reflection;
using Maksov.VideoService.Comments.Bll.Ports;
using Maksov.VideoService.Comments.Dal.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Comments.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Assembly with embedded SQL migrations (for SqlMigrator).</summary>
    public static Assembly MigrationsAssembly => typeof(DalServiceCollectionExtensions).Assembly;

    public static IServiceCollection AddDal(this IServiceCollection services)
    {
        services.AddSingleton<ICommentRepository, PostgresCommentRepository>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }
}
