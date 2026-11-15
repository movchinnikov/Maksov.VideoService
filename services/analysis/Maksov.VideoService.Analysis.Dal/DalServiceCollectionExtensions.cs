using System.Reflection;
using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.Dal.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Analysis.Dal;

public static class DalServiceCollectionExtensions
{
    /// <summary>Assembly with embedded SQL migrations (for SqlMigrator).</summary>
    public static Assembly MigrationsAssembly => typeof(DalServiceCollectionExtensions).Assembly;

    /// <summary>Postgres store and queue + database readiness check.</summary>
    public static IServiceCollection AddDal(this IServiceCollection services)
    {
        services.AddSingleton<PostgresAnalysisStore>();
        services.AddSingleton<IAnalysisRepository>(static sp => sp.GetRequiredService<PostgresAnalysisStore>());
        services.AddSingleton<IAnalysisQueue>(static sp => sp.GetRequiredService<PostgresAnalysisStore>());
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        return services;
    }
}
