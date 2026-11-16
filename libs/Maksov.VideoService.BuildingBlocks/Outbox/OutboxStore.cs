#pragma warning disable CA2007 // await using declarations cannot take ConfigureAwait; the calls below do.
using Npgsql;

namespace Maksov.VideoService.BuildingBlocks.Outbox;

/// <summary>Signal that something may be pending (LISTEN/NOTIFY) or the timeout elapsed.</summary>
public interface IOutboxSignal : IAsyncDisposable
{
    Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Postgres outbox: table <c>outbox</c> with one row per (event, subscriber), claimed with <c>FOR UPDATE SKIP LOCKED</c>
/// and a lease. The table is created by each service's own migration (see <see cref="OutboxSql.CreateTable"/>).
/// </summary>
public sealed class OutboxStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Writes one row per subscriber inside the caller's transaction (so the event is stored atomically with the business change)
    /// and notifies the dispatcher. Repeating the same <paramref name="eventId"/> is a no-op.
    /// </summary>
    public static async Task EnqueueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        string topic,
        string payloadJson,
        IReadOnlyCollection<string> subscribers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(subscribers);
        foreach (var subscriber in subscribers)
        {
            await using var cmd = new NpgsqlCommand(
                "insert into outbox (id, subscriber, topic, payload) values ($1, $2, $3, $4::jsonb) on conflict do nothing", connection, transaction);
            cmd.Parameters.AddWithValue(eventId);
            cmd.Parameters.AddWithValue(subscriber);
            cmd.Parameters.AddWithValue(topic);
            cmd.Parameters.AddWithValue(payloadJson);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var notify = new NpgsqlCommand("select pg_notify('outbox', $1)", connection, transaction);
        notify.Parameters.AddWithValue(eventId.ToString());
        await notify.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IOutboxSignal> ListenAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var cmd = new NpgsqlCommand("listen outbox", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new Signal(connection);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(
        IReadOnlyCollection<string> subscribers, int limit, TimeSpan lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscribers);
        await using var cmd = dataSource.CreateCommand("""
            update outbox o
               set locked_until = now() + $2, attempts = o.attempts + 1
              from (select id, subscriber from outbox
                     where sent_at is null and next_attempt_at <= now()
                       and subscriber = any($3)
                       and (locked_until is null or locked_until < now())
                     order by created_at
                     limit $1
                       for update skip locked) c
             where o.id = c.id and o.subscriber = c.subscriber
            returning o.id, o.subscriber, o.topic, o.payload::text, o.attempts
            """);
        cmd.Parameters.AddWithValue(limit);
        cmd.Parameters.AddWithValue(lease);
        cmd.Parameters.AddWithValue(subscribers.ToArray());
        var result = new List<OutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new OutboxMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }

        return result;
    }

    public async Task MarkSentAsync(Guid id, string subscriber, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand(
            "update outbox set sent_at = now(), locked_until = null, last_error = null where id = $1 and subscriber = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(subscriber);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(Guid id, string subscriber, string details, TimeSpan delay, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("""
            update outbox set next_attempt_at = now() + $3, locked_until = null, last_error = left($4, 2000)
             where id = $1 and subscriber = $2
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(subscriber);
        cmd.Parameters.AddWithValue(delay);
        cmd.Parameters.AddWithValue(details);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Signal(NpgsqlConnection connection) : IOutboxSignal
    {
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            connection.WaitAsync(timeout, cancellationToken);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}

/// <summary>The outbox table every publishing service creates in its own database (copy into a migration).</summary>
public static class OutboxSql
{
    public const string CreateTable = """
        create table outbox (
            id              uuid        not null,
            subscriber      text        not null,
            topic           text        not null,
            payload         jsonb       not null,
            attempts        int         not null default 0,
            next_attempt_at timestamptz not null default now(),
            locked_until    timestamptz,
            sent_at         timestamptz,
            last_error      text,
            created_at      timestamptz not null default now(),
            primary key (id, subscriber)
        );

        create index ix_outbox_pending on outbox (subscriber, next_attempt_at) where sent_at is null;
        """;
}
