using Maksov.VideoService.Videos.MediaWorker.Ports;
using Npgsql;

namespace Maksov.VideoService.Videos.MediaWorker.Adapters.Postgres;

internal sealed class PostgresOutbox(NpgsqlDataSource dataSource) : IOutbox
{
    public async Task<IJobSignal> ListenAsync(CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var cmd = new NpgsqlCommand("listen outbox", connection))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
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
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed class Signal(NpgsqlConnection connection) : IJobSignal
    {
        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            connection.WaitAsync(timeout, cancellationToken);

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
