using Npgsql;

namespace Maksov.VideoService.TestSupport;

/// <summary>
/// Postgres adapter tests: each test suite gets its own database inside the test Postgres (<c>VIDEOSERVICE_TEST_POSTGRES</c>).
/// Without the environment variable it returns <c>null</c> and the tests are skipped.
/// </summary>
public static class TestDatabases
{
    public static string? BaseConnectionString { get; } = Environment.GetEnvironmentVariable("VIDEOSERVICE_TEST_POSTGRES");

    public static async Task<NpgsqlDataSource?> CreateAsync(string name, CancellationToken ct)
    {
        if (BaseConnectionString is null)
        {
            return null;
        }

        var database = $"it_{name}";
        var admin = NpgsqlDataSource.Create(BaseConnectionString);
        await using (admin.ConfigureAwait(false))
        {
            var exists = admin.CreateCommand("select 1 from pg_database where datname = $1");
            await using (exists.ConfigureAwait(false))
            {
                exists.Parameters.AddWithValue(database);
                if (await exists.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                {
                    var create = admin.CreateCommand($"create database {database}");
                    await using (create.ConfigureAwait(false))
                    {
                        await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
            }
        }

        var builder = new NpgsqlConnectionStringBuilder(BaseConnectionString) { Database = database };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }
}
