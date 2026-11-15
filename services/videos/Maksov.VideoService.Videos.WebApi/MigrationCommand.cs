using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Dal;

namespace Maksov.VideoService.Videos.WebApi;

/// <summary>One-shot mode of the migrator container: DB migrations and bucket creation.</summary>
internal static class MigrationCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string command, CancellationToken ct)
    {
        var migrator = services.GetRequiredService<SqlMigrator>();
        var assembly = DalServiceCollectionExtensions.MigrationsAssembly;

        if (command == "--rollback-last")
        {
            await migrator.RollbackLastAsync(assembly, ct);
            return 0;
        }

        await migrator.MigrateAsync(assembly, ct);
        await services.GetRequiredService<IObjectStorage>().EnsureBucketAsync(ct);
        return 0;
    }
}
