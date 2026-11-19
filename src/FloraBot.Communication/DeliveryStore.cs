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
            ALTER TABLE communication.email_deliveries DROP CONSTRAINT IF EXISTS email_deliveries_state_check;
            ALTER TABLE communication.email_deliveries ADD CONSTRAINT email_deliveries_state_check
              CHECK(state IN ('SENDING','ACCEPTED','UNCERTAIN','RETRYABLE'));
            CREATE TABLE IF NOT EXISTS communication.email_queue (
                id text PRIMARY KEY,external_id text NOT NULL UNIQUE,fingerprint text NOT NULL,
                protected_payload text,state text NOT NULL DEFAULT 'PREPARED',revision bigint NOT NULL DEFAULT 0,
                updated_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS communication.email_reconciliations (
                operation_id uuid PRIMARY KEY,delivery_id text NOT NULL,request_hash text NOT NULL,
                result_state text NOT NULL,revision bigint NOT NULL,actor_id uuid NOT NULL,
                provider_reference text NOT NULL,reason text NOT NULL,created_at timestamptz NOT NULL DEFAULT now()
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
        await using var retry = data.CreateCommand("""
            UPDATE communication.email_deliveries SET state='SENDING',updated_at=now()
            WHERE id=@id AND fingerprint=@fingerprint AND state='RETRYABLE' RETURNING id
            """);
        retry.Parameters.AddWithValue("id", id);
        retry.Parameters.AddWithValue("fingerprint", fingerprint);
        if (await retry.ExecuteScalarAsync(ct) is not null) return new(true, fingerprint, "SENDING");
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
