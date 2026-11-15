using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Maksov.VideoService.BuildingBlocks.Postgres;

/// <summary>
/// Minimal SQL migration runner over assembly embedded resources.
/// Files: <c>NNNN_name.sql</c> (forward) and <c>NNNN_name.down.sql</c> (rollback).
/// Application is serialized by an advisory lock; each migration runs in its own transaction.
/// </summary>
public sealed partial class SqlMigrator(NpgsqlDataSource dataSource, ILogger<SqlMigrator> logger)
{
    private const long AdvisoryLockKey = 0x5649_4445_4f53; // "VIDEOS"

    public async Task MigrateAsync(Assembly assembly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var migrations = Discover(assembly).Where(static m => !m.IsDown).OrderBy(static m => m.Version, StringComparer.Ordinal).ToList();

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
            var applied = await GetAppliedAsync(connection, cancellationToken).ConfigureAwait(false);

            foreach (var migration in migrations.Where(m => !applied.Contains(m.Version)))
            {
                var sql = await ReadAsync(assembly, migration.ResourceName).ConfigureAwait(false);
                await ApplyAsync(connection, sql, "insert into schema_migrations(version) values ($1)", migration.Version, cancellationToken)
                    .ConfigureAwait(false);
                LogApplied(logger, migration.Version);
            }
        }
    }

    public async Task RollbackLastAsync(Assembly assembly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            await PrepareAsync(connection, cancellationToken).ConfigureAwait(false);
            var applied = await GetAppliedAsync(connection, cancellationToken).ConfigureAwait(false);
            var last = applied.Order(StringComparer.Ordinal).LastOrDefault();
            if (last is null)
            {
                return;
            }

            var down = Discover(assembly).SingleOrDefault(m => m.IsDown && m.Version == last)
                ?? throw new InvalidOperationException($"No down migration for {last}");
            var sql = await ReadAsync(assembly, down.ResourceName).ConfigureAwait(false);
            await ApplyAsync(connection, sql, "delete from schema_migrations where version = $1", last, cancellationToken).ConfigureAwait(false);
            LogRolledBack(logger, last);
        }
    }

    private static async Task PrepareAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        // The session lock is released when the connection closes.
        await ExecuteAsync(connection, $"select pg_advisory_lock({AdvisoryLockKey})", ct).ConfigureAwait(false);
        await ExecuteAsync(connection, """
            create table if not exists schema_migrations (
                version    text primary key,
                applied_at timestamptz not null default now()
            )
            """, ct).ConfigureAwait(false);
    }

    private static async Task ApplyAsync(NpgsqlConnection connection, string sql, string historySql, string version, CancellationToken ct)
    {
        var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            await ExecuteAsync(connection, sql, ct).ConfigureAwait(false);
            var history = new NpgsqlCommand(historySql, connection);
            await using (history.ConfigureAwait(false))
            {
                history.Parameters.AddWithValue(version);
                await history.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<string>> GetAppliedAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var cmd = new NpgsqlCommand("select version from schema_migrations", connection);
        await using (cmd.ConfigureAwait(false))
        {
            var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    result.Add(reader.GetString(0));
                }
            }
        }
        return result;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var cmd = new NpgsqlCommand(sql, connection);
        await using (cmd.ConfigureAwait(false))
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadAsync(Assembly assembly, string resourceName)
    {
        var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Resource {resourceName} not found");
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }
    }

    private static IEnumerable<Migration> Discover(Assembly assembly)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            // Resource name: <Namespace>.Migrations.0001_init.sql
            var withoutExt = name[..^".sql".Length];
            var isDown = withoutExt.EndsWith(".down", StringComparison.Ordinal);
            if (isDown)
            {
                withoutExt = withoutExt[..^".down".Length];
            }
            var fileName = withoutExt[(withoutExt.LastIndexOf('.') + 1)..];
            yield return new Migration(fileName, name, isDown);
        }
    }

    private sealed record Migration(string Version, string ResourceName, bool IsDown);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied migration {Version}")]
    private static partial void LogApplied(ILogger logger, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rolled back migration {Version}")]
    private static partial void LogRolledBack(ILogger logger, string version);
}
