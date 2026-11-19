using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Communication;

public sealed class EmailReconciliation(NpgsqlDataSource data, ICapPublisher publisher, IConfiguration config)
{
    public async Task<IResult> ReconcileAsync(ReconcileEmailRequest input, CancellationToken ct)
    {
        if (input.OperationId == Guid.Empty || input.ActorId == Guid.Empty || string.IsNullOrWhiteSpace(input.DeliveryId) ||
            input.DeliveryId.Length > 200 || input.ExpectedRevision < 0 || input.ExpectedState is not ("SENDING" or "UNCERTAIN") ||
            input.Decision is not ("ACCEPTED" or "RETRY_CONFIRMED_NOT_SENT") ||
            string.IsNullOrWhiteSpace(input.ProviderReference) || input.ProviderReference.Length > 200 ||
            string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Length is < 10 or > 1000)
            return Results.Problem(statusCode: 400, detail: "An operation ID, expected state/revision, provider reference and reason are required.");
        var hash = CommunicationEvents.Hash(JsonSerializer.Serialize(input));
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@operation,0))", connection, transaction);
        guard.Parameters.AddWithValue("operation", "email-reconciliation:" + input.OperationId);
        await guard.ExecuteNonQueryAsync(ct);
        await using var previous = new NpgsqlCommand("SELECT request_hash,result_state,revision FROM communication.email_reconciliations WHERE operation_id=@id", connection, transaction);
        previous.Parameters.AddWithValue("id", input.OperationId);
        await using (var reader = await previous.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
                return reader.GetString(0) == hash ? Results.Ok(new { state = reader.GetString(1), revision = reader.GetInt64(2) })
                    : Results.Problem(statusCode: 409, detail: "Operation ID belongs to a different reconciliation.");

        await using var query = new NpgsqlCommand("""
            SELECT q.revision,d.state,d.updated_at<now()-interval '60 seconds'
            FROM communication.email_queue q JOIN communication.email_deliveries d ON d.id=q.id
            WHERE q.external_id=@id FOR UPDATE OF q,d
            """, connection, transaction);
        query.Parameters.AddWithValue("id", input.DeliveryId);
        long revision;
        string current;
        bool stale;
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            revision = reader.GetInt64(0); current = reader.GetString(1); stale = reader.GetBoolean(2);
        }
        if (revision != input.ExpectedRevision || current != input.ExpectedState || (current == "SENDING" && !stale))
            return Results.Problem(statusCode: 409, detail: "Delivery changed or its attempt is still active.");
        var accepted = input.Decision == "ACCEPTED";
        var state = accepted ? "ACCEPTED" : "RETRYABLE";
        revision++;
        await using var update = new NpgsqlCommand("""
            UPDATE communication.email_deliveries SET state=@state,updated_at=now()
              WHERE id=(SELECT id FROM communication.email_queue WHERE external_id=@id);
            UPDATE communication.email_queue SET state=@queue,revision=@revision,updated_at=now(),
              protected_payload=CASE WHEN @queue='ACCEPTED' THEN NULL ELSE protected_payload END WHERE external_id=@id;
            INSERT INTO communication.email_reconciliations(operation_id,delivery_id,request_hash,result_state,revision,actor_id,provider_reference,reason)
              VALUES(@operation,@id,@hash,@state,@revision,@actor,@reference,@reason);
            """, connection, transaction);
        update.Parameters.AddWithValue("state", state);
        update.Parameters.AddWithValue("queue", accepted ? "ACCEPTED" : "PREPARED");
        update.Parameters.AddWithValue("revision", revision);
        update.Parameters.AddWithValue("id", input.DeliveryId);
        update.Parameters.AddWithValue("operation", input.OperationId);
        update.Parameters.AddWithValue("hash", hash);
        update.Parameters.AddWithValue("actor", input.ActorId);
        update.Parameters.AddWithValue("reference", input.ProviderReference.Trim());
        update.Parameters.AddWithValue("reason", input.Reason.Trim());
        await update.ExecuteNonQueryAsync(ct);
        var resultState = accepted ? "ACCEPTED" : "QUEUED";
        var token = config["COMMUNICATION_SERVICE_TOKEN"]!;
        await publisher.PublishAsync(CommunicationEvents.Outcome, new EmailDeliveryOutcome(input.DeliveryId, resultState,
            revision, CommunicationEvents.SignOutcome(input.DeliveryId, resultState, revision, token)), cancellationToken: ct);
        if (!accepted)
            await publisher.PublishAsync(CommunicationEvents.Requested,
                new QueuedEmailDelivery(input.DeliveryId, CommunicationEvents.SignRequest(input.DeliveryId, token)), cancellationToken: ct);
        await outbox.CommitAsync(ct);
        return Results.Ok(new { state, revision });
    }
}
