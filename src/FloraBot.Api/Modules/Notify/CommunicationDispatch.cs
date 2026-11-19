using DotNetCore.CAP;
using FloraBot.Api.Modules.Identity;
using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed class CommunicationDispatch(NpgsqlDataSource data, ICapPublisher publisher,
    CommunicationEmailSender sender, EmailDelivery delivery, EmailSettings settings, IConfiguration config)
{
    public bool Enabled => config.GetValue<bool>("COMMUNICATION_BROKER_ENABLED");

    public async Task QueueAsync(SellerEmailRequested business, SellerEmailContent content, CancellationToken ct)
    {
        await using var connection = await data.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@batch,0))", connection, transaction);
        guard.Parameters.AddWithValue("batch", "communication:" + content.DeliveryId);
        await guard.ExecuteNonQueryAsync(ct);
        await using var existing = new NpgsqlCommand("SELECT delivery_id FROM notify.communication_deliveries WHERE batch_key=@batch ORDER BY delivery_id", connection, transaction);
        existing.Parameters.AddWithValue("batch", content.DeliveryId);
        var ids = new List<string>();
        await using (var reader = await existing.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        var recipients = content.Recipients.Select(recipient => (Recipient: recipient,
            Id: delivery.Key($"{content.DeliveryId}:{recipient.UserId}"))).ToArray();
        if (ids.Count == 0)
        {
            foreach (var row in recipients)
                await sender.StageAsync(new(row.Id, new(row.Recipient.Email, content.Subject, content.Text)), ct);
            ids.AddRange(recipients.Select(row => row.Id));
        }

        foreach (var row in recipients.Where(row => ids.Contains(row.Id)))
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO notify.communication_deliveries(delivery_id,batch_key,seller_id,recipient_user_id,kind,subscription_id)
                VALUES(@id,@batch,@seller,@user,@kind,@subscription) ON CONFLICT(delivery_id) DO NOTHING
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", row.Id);
            insert.Parameters.AddWithValue("batch", content.DeliveryId);
            insert.Parameters.AddWithValue("seller", business.SellerId);
            insert.Parameters.AddWithValue("user", row.Recipient.UserId);
            insert.Parameters.AddWithValue("kind", business.Kind);
            insert.Parameters.AddWithValue("subscription", (object?)business.SubscriptionId ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }
        foreach (var id in ids)
            await publisher.PublishAsync(CommunicationEvents.Requested,
                new QueuedEmailDelivery(id, CommunicationEvents.SignRequest(id, settings.Credential)), cancellationToken: ct);
        await outbox.CommitAsync(ct);
    }
}
