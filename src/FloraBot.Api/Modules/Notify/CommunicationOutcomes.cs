using DotNetCore.CAP;
using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed class CommunicationOutcomes(NpgsqlDataSource data, IConfiguration config) : ICapSubscribe
{
    [CapSubscribe(CommunicationEvents.Outcome)]
    public async Task HandleAsync(EmailDeliveryOutcome outcome)
    {
        if (!CommunicationEvents.ValidOutcome(outcome, config) || outcome.Revision < 1 ||
            outcome.State is not ("ACCEPTED" or "SKIPPED" or "UNCERTAIN" or "QUEUED")) return;
        await using var connection = await data.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var query = new NpgsqlCommand("SELECT batch_key FROM notify.communication_deliveries WHERE delivery_id=@id", connection, transaction);
        query.Parameters.AddWithValue("id", outcome.DeliveryId);
        if (await query.ExecuteScalarAsync() is not string batch) return;
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@batch,0))", connection, transaction);
        guard.Parameters.AddWithValue("batch", "communication:" + batch);
        await guard.ExecuteNonQueryAsync();
        await using var update = new NpgsqlCommand("""
            UPDATE notify.communication_deliveries SET state=@state,outcome_revision=@revision,updated_at=now()
            WHERE delivery_id=@id AND outcome_revision<@revision
            RETURNING kind,subscription_id
            """, connection, transaction);
        update.Parameters.AddWithValue("state", outcome.State);
        update.Parameters.AddWithValue("revision", outcome.Revision);
        update.Parameters.AddWithValue("id", outcome.DeliveryId);
        string? kind = null;
        Guid? subscription = null;
        await using (var reader = await update.ExecuteReaderAsync())
            if (await reader.ReadAsync()) { kind = reader.GetString(0); subscription = reader.IsDBNull(1) ? null : reader.GetGuid(1); }
        if (kind == "subscription-reminder" && subscription is Guid id)
        {
            await using var aggregate = new NpgsqlCommand("""
                SELECT CASE WHEN bool_and(state='ACCEPTED') THEN 'SENT'
                  WHEN bool_or(state='UNCERTAIN') THEN 'FAILED'
                  WHEN bool_and(state IN ('ACCEPTED','SKIPPED')) THEN 'SKIPPED' END
                FROM notify.communication_deliveries WHERE batch_key=@batch
                """, connection, transaction);
            aggregate.Parameters.AddWithValue("batch", batch);
            if (await aggregate.ExecuteScalarAsync() is string state)
            {
                await using var audit = new NpgsqlCommand("""
                    SELECT flow.audit(NULL,@action,'subscriptions',@id,'{}'::jsonb)
                    WHERE NOT EXISTS(SELECT 1 FROM notify.audit_logs WHERE entity_type='subscriptions'
                      AND entity_id=@id AND action=@action)
                    """, connection, transaction);
                audit.Parameters.AddWithValue("id", id);
                audit.Parameters.AddWithValue("action", "SUBSCRIPTION_REMINDER_" + state);
                await audit.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }
}
