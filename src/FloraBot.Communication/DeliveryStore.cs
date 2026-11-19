using Npgsql;

namespace FloraBot.Communication;

public sealed record DeliveryClaim(bool Acquired, string Fingerprint, string State);

public sealed class DeliveryStore(NpgsqlDataSource data) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(hashtextextended('communication:delivery-schema:v1',0));
            CREATE SCHEMA IF NOT EXISTS communication;
            CREATE TABLE IF NOT EXISTS communication.email_deliveries (
                id text PRIMARY KEY CHECK(length(id)=64),
                fingerprint text NOT NULL CHECK(length(fingerprint)=64),
                state text NOT NULL CHECK(state IN ('SENDING','ACCEPTED','UNCERTAIN')),
                created_at timestamptz NOT NULL DEFAULT now(),
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<DeliveryClaim> ClaimAsync(string id, string fingerprint, CancellationToken ct)
    {
        await using var insert = data.CreateCommand("""
            INSERT INTO communication.email_deliveries(id,fingerprint,state)
            VALUES(@id,@fingerprint,'SENDING') ON CONFLICT(id) DO NOTHING RETURNING id
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("fingerprint", fingerprint);
        if (await insert.ExecuteScalarAsync(ct) is not null) return new(true, fingerprint, "SENDING");
        await using var query = data.CreateCommand("SELECT fingerprint,state FROM communication.email_deliveries WHERE id=@id");
        query.Parameters.AddWithValue("id", id);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Delivery receipt is missing.");
        return new(false, reader.GetString(0), reader.GetString(1));
    }

    public async Task SetStateAsync(string id, string state, CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            UPDATE communication.email_deliveries SET state=@state,updated_at=now()
            WHERE id=@id AND state='SENDING'
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("state", state);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
            throw new InvalidOperationException("Delivery state could not be confirmed.");
    }
}
