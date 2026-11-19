using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace FloraBot.Communication;

public sealed class EmailQueue(NpgsqlDataSource data, IDataProtectionProvider protection,
    IConfiguration config, ICapPublisher publisher)
{
    private readonly IDataProtector protector = protection.CreateProtector("FloraBot.Communication.EmailQueue.v1");

    public async Task<IResult> StageAsync(StagedEmailRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DeliveryId) || request.DeliveryId.Length > 200 || request.Message is null)
            return Results.Problem(statusCode: 400, detail: "Invalid email request.");
        try { TransactionalEmailSender.Validate(request.Message); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, detail: "Invalid email content."); }
        var message = request.Message with { DeliveryId = null };
        var fingerprint = CommunicationEvents.Fingerprint(message);
        await using var insert = data.CreateCommand("""
            INSERT INTO communication.email_queue(id,external_id,fingerprint,protected_payload)
            VALUES(@id,@external,@fingerprint,@payload) ON CONFLICT(id) DO NOTHING
            """);
        insert.Parameters.AddWithValue("id", CommunicationEvents.Hash(request.DeliveryId));
        insert.Parameters.AddWithValue("external", request.DeliveryId);
        insert.Parameters.AddWithValue("fingerprint", fingerprint);
        insert.Parameters.AddWithValue("payload", protector.Protect(JsonSerializer.Serialize(message)));
        await insert.ExecuteNonQueryAsync(ct);
        await using var check = data.CreateCommand("SELECT fingerprint FROM communication.email_queue WHERE external_id=@id");
        check.Parameters.AddWithValue("id", request.DeliveryId);
        if (await check.ExecuteScalarAsync(ct) is not string stored || stored != fingerprint)
            return Results.Problem(statusCode: 409, detail: "Delivery ID belongs to different content.");
        return Results.Ok(new EmailDeliveryReceipt("PREPARED"));
    }

    public async Task<(TransactionalEmail? Message, string Fingerprint, string? State)> ReadAsync(string external, CancellationToken ct)
    {
        await using var query = data.CreateCommand("""
            SELECT q.protected_payload,q.fingerprint,CASE WHEN d.state IN ('SENDING','ACCEPTED','UNCERTAIN') THEN d.state ELSE q.state END FROM communication.email_queue q
            LEFT JOIN communication.email_deliveries d ON d.id=q.id WHERE q.external_id=@id
            """);
        query.Parameters.AddWithValue("id", external);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Staged email is missing.");
        var message = reader.IsDBNull(0) ? null : JsonSerializer.Deserialize<TransactionalEmail>(protector.Unprotect(reader.GetString(0)));
        return (message, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task CompleteAsync(string external, string desiredState, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        await using var query = new NpgsqlCommand("""
            SELECT q.state,q.revision,CASE WHEN d.state IN ('ACCEPTED','UNCERTAIN','SENDING')
              THEN d.state ELSE @desired END FROM communication.email_queue q
            LEFT JOIN communication.email_deliveries d ON d.id=q.id WHERE q.external_id=@id FOR UPDATE OF q
            """, connection, transaction);
        query.Parameters.AddWithValue("id", external);
        query.Parameters.AddWithValue("desired", desiredState);
        string oldState, state;
        long revision;
        await using (var reader = await query.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Email request is missing.");
            oldState = reader.GetString(0); revision = reader.GetInt64(1); state = reader.GetString(2);
        }
        if (state is not ("ACCEPTED" or "UNCERTAIN" or "SKIPPED")) throw new InvalidOperationException("Email delivery is still pending.");
        if (oldState != state)
        {
            revision++;
            await using var update = new NpgsqlCommand("""
                UPDATE communication.email_queue SET state=@state,revision=@revision,updated_at=now(),
                  protected_payload=CASE WHEN @state IN ('ACCEPTED','SKIPPED') THEN NULL ELSE protected_payload END
                WHERE external_id=@id
                """, connection, transaction);
            update.Parameters.AddWithValue("state", state);
            update.Parameters.AddWithValue("revision", revision);
            update.Parameters.AddWithValue("id", external);
            await update.ExecuteNonQueryAsync(ct);
        }
        await PublishOutcomeAsync(external, state, revision, ct);
        await outbox.CommitAsync(ct);
    }

    public Task PublishOutcomeAsync(string id, string state, long revision, CancellationToken ct) =>
        publisher.PublishAsync(CommunicationEvents.Outcome, new EmailDeliveryOutcome(id, state, revision,
            CommunicationEvents.SignOutcome(id, state, revision, config["COMMUNICATION_SERVICE_TOKEN"]!)), cancellationToken: ct);
}
