using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public static class EmailReminderAudit
{
    public static async Task<bool> WasSentAsync(NpgsqlDataSource data, Guid subscriptionId)
    {
        await using var query = data.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM notify.audit_logs
              WHERE entity_type='subscriptions' AND entity_id=@id AND action='SUBSCRIPTION_REMINDER_SENT')
            """);
        query.Parameters.AddWithValue("id", subscriptionId);
        return await query.ExecuteScalarAsync() is true;
    }

    // Called under the reminder job's database transaction/advisory lock.
    public static async Task<bool> MarkQueuedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid subscriptionId, CancellationToken ct)
    {
        await using var check = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM notify.audit_logs
              WHERE action='SUBSCRIPTION_REMINDER_QUEUED' AND entity_type='subscriptions' AND entity_id=@subscription)
            """, connection, transaction);
        check.Parameters.AddWithValue("subscription", subscriptionId);
        if (await check.ExecuteScalarAsync(ct) is true) return false;
        await using var audit = new NpgsqlCommand("SELECT flow.audit(NULL,'SUBSCRIPTION_REMINDER_QUEUED','subscriptions',@subscription,'{}'::jsonb)", connection, transaction);
        audit.Parameters.AddWithValue("subscription", subscriptionId);
        await audit.ExecuteNonQueryAsync(ct);
        return true;
    }

    public static async Task RecordOutcomeAsync(NpgsqlDataSource data, Guid subscriptionId, string outcome)
    {
        await using var audit = data.CreateCommand("SELECT flow.audit(NULL,@outcome,'subscriptions',@subscription,'{}'::jsonb)");
        audit.Parameters.AddWithValue("outcome", outcome);
        audit.Parameters.AddWithValue("subscription", subscriptionId);
        await audit.ExecuteNonQueryAsync();
    }
}
