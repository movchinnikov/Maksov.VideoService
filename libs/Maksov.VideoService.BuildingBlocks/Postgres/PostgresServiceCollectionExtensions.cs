using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Maksov.VideoService.BuildingBlocks.Postgres;

public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers a single <see cref="NpgsqlDataSource"/> per application (connection pool).
    /// Connection string — <c>ConnectionStrings:Postgres</c>.
    /// </summary>
    public static IServiceCollection AddPostgres(this IServiceCollection services, IConfiguration configuration, string applicationName)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required");

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.ConnectionStringBuilder.ApplicationName = applicationName;
            return builder.Build();
        });
        return services;
    }
}

