using System.Globalization;
using System.Text.Json.Serialization;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record SellerEmailRequested(
    [property: JsonPropertyName("seller_id")] Guid SellerId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("subscription_id")] Guid? SubscriptionId = null,
    [property: JsonPropertyName("expires_on")] DateOnly? ExpiresOn = null);

public static class SellerEmailEvents
{
    public const string Requested = "florabot.seller.email-requested.v1";
    public const string Approval = "approval";
    public const string Receipt = "subscription-receipt";
    public const string Reminder = "subscription-reminder";

    public static Task PublishApprovalAsync(ICapPublisher publisher, Guid sellerId, CancellationToken ct) =>
        publisher.PublishAsync(Requested, new SellerEmailRequested(sellerId, Approval), cancellationToken: ct);

    public static Task PublishReceiptAsync(ICapPublisher publisher, Guid sellerId, Guid subscriptionId, CancellationToken ct) =>
        publisher.PublishAsync(Requested, new SellerEmailRequested(sellerId, Receipt, subscriptionId), cancellationToken: ct);
}

public sealed record SellerEmailRecipient(Guid UserId, string Email);
public sealed record SellerEmailContent(string DeliveryId, string Subject, string Text, IReadOnlyList<SellerEmailRecipient> Recipients);

public sealed class SellerEmailReader(NpgsqlDataSource data)
{
    public async Task<SellerEmailContent?> ReadAsync(SellerEmailRequested message, CancellationToken ct)
    {
        if (message.SellerId == Guid.Empty || message.Kind is not (SellerEmailEvents.Approval or SellerEmailEvents.Receipt or SellerEmailEvents.Reminder) ||
            (message.Kind == SellerEmailEvents.Receipt && !message.SubscriptionId.HasValue) ||
            (message.Kind == SellerEmailEvents.Approval && message.SubscriptionId.HasValue))
            throw new ArgumentException("Invalid seller email event.", nameof(message));
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await using var seller = new NpgsqlCommand("""
            SELECT shop_name,status
            FROM identity.sellers WHERE id=@id
            """, connection, transaction);
        seller.Parameters.AddWithValue("id", message.SellerId);
        string shop; string status;
        await using (var reader = await seller.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Seller email resource is missing.");
            shop = reader.GetString(0); status = reader.GetString(1);
        }
        string subject; string text; string deliveryId;
        if (message.Kind == SellerEmailEvents.Approval)
        {
            if (status is "SUSPENDED" or "CLOSED") return null;
            if (status == "PENDING") throw new InvalidOperationException("Seller approval has not committed.");
            subject = "FloraBot: Shop của bạn đã được duyệt";
            text = $"Chào {shop},\n\nHồ sơ shop đã được FloraBot duyệt. Bạn có thể đăng nhập cổng quản lý để xem gói thuê và thực hiện thanh toán. Việc duyệt hồ sơ chưa đồng nghĩa với đã thanh toán hay đã được cấp ô tủ.\n\nCảm ơn bạn đã cùng FloraBot mở thêm một góc hoa.";
            deliveryId = $"approval:{message.SellerId}";
        }
        else if (message.Kind == SellerEmailEvents.Reminder)
        {
            if (!message.SubscriptionId.HasValue) return null;
            await using var lease = new NpgsqlCommand(
                SubscriptionReminders.EligibleLeases + " AND sub.id=@id AND sub.seller_id=@seller", connection, transaction);
            lease.Parameters.AddWithValue("id", message.SubscriptionId.Value);
            lease.Parameters.AddWithValue("seller", message.SellerId);
            await using var reader = await lease.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var date = reader.GetDateTime(4).AddHours(7).ToString("HH:mm dd/MM/yyyy", CultureInfo.InvariantCulture);
            subject = "FloraBot: Kỳ thuê kiosk sắp hết hạn";
            text = $"Chào {shop},\n\nKỳ thuê kiosk {reader.GetString(2)} ({reader.GetString(3)}) sẽ hết hạn lúc {date} giờ Việt Nam. Bạn có thể gia hạn kiosk này trong cổng Seller.";
            deliveryId = $"subscription-reminder:{message.SubscriptionId}";
        }
        else
        {
            await using var subscription = new NpgsqlCommand("""
                SELECT price,period_from,period_to FROM identity.subscriptions
                WHERE id=@id AND seller_id=@seller AND status IN ('ACTIVE','EXPIRED') AND payment_id IS NOT NULL
                """, connection, transaction);
            subscription.Parameters.AddWithValue("id", message.SubscriptionId!.Value);
            subscription.Parameters.AddWithValue("seller", message.SellerId);
            await using var reader = await subscription.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Paid subscription does not match the seller email event.");
            var price = reader.GetInt64(0).ToString("N0", CultureInfo.GetCultureInfo("vi-VN"));
            var from = reader.GetFieldValue<DateOnly>(1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var to = reader.GetFieldValue<DateOnly>(2).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            subject = "FloraBot: Đã nhận thanh toán gói thuê";
            text = $"Chào {shop},\n\nFloraBot đã ghi nhận thanh toán {price} đ cho gói thuê của shop.\nMã đăng ký gói: {message.SubscriptionId}\nKỳ thuê: từ {from} đến trước ngày {to}.\n\nBạn có thể xem chi tiết tại cổng quản lý. Đây là xác nhận thanh toán, không thay thế hóa đơn thuế.";
            deliveryId = $"subscription-receipt:{message.SubscriptionId}";
        }
        await using var users = new NpgsqlCommand("""
            SELECT u.id,
                   COALESCE(
                       CASE WHEN p.contact_email_verified_at IS NOT NULL AND btrim(p.contact_email) <> '' THEN p.contact_email END,
                       u.email
                   ) AS email
            FROM identity.users u
            LEFT JOIN identity.user_profiles p ON p.user_id = u.id
            WHERE u.seller_id=@seller AND u.role='SELLER' AND u.status='ACTIVE'
              AND COALESCE(
                       CASE WHEN p.contact_email_verified_at IS NOT NULL AND btrim(p.contact_email) <> '' THEN p.contact_email END,
                       u.email
                   ) IS NOT NULL
            ORDER BY u.id
            """, connection, transaction);
        users.Parameters.AddWithValue("seller", message.SellerId);
        var recipients = new List<SellerEmailRecipient>();
        await using (var reader = await users.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (EmailSettings.IsAddress(reader.GetString(1))) recipients.Add(new(reader.GetGuid(0), reader.GetString(1)));
        await transaction.CommitAsync(ct);
        if (recipients.Count == 0) throw new InvalidOperationException("Seller has no active email recipient. Provision an account, then retry the event.");
        return new(deliveryId, subject, text, recipients);
    }
}

public sealed class SellerEmailSubscriber(EmailSettings settings, SellerEmailReader reader, EmailDelivery delivery, NpgsqlDataSource data,
    ILogger<SellerEmailSubscriber> logger) : ICapSubscribe
{
    [CapSubscribe(SellerEmailEvents.Requested)]
    public async Task HandleAsync(SellerEmailRequested message)
    {
        if (settings.Provider == EmailProvider.Disabled)
        {
            logger.LogInformation("Seller email delivery is disabled; event was not sent");
            return;
        }
        var reminderId = message.Kind == SellerEmailEvents.Reminder ? message.SubscriptionId : null;
        if (reminderId is Guid previous && await EmailReminderAudit.WasSentAsync(data, previous)) return;
        try
        {
            var content = await reader.ReadAsync(message, CancellationToken.None);
            if (content is null)
            {
                if (reminderId is Guid skipped)
                    await EmailReminderAudit.RecordOutcomeAsync(data, skipped, "SUBSCRIPTION_REMINDER_SKIPPED");
                return;
            }
            foreach (var recipient in content.Recipients)
                await delivery.SendOnceAsync($"{content.DeliveryId}:{recipient.UserId}",
                    new(recipient.Email, content.Subject, content.Text), CancellationToken.None);
            if (reminderId is Guid sent)
                await EmailReminderAudit.RecordOutcomeAsync(data, sent, "SUBSCRIPTION_REMINDER_SENT");
        }
        catch (Exception) when (reminderId.HasValue)
        {
            await EmailReminderAudit.RecordOutcomeAsync(data, reminderId.Value, "SUBSCRIPTION_REMINDER_FAILED");
            throw;
        }
    }
}
