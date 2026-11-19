using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Modules.Notify;

namespace FloraBot.Communication;

public sealed class EmailDeliveryService(DeliveryStore store, ITransactionalEmailSender sender, EmailSettings settings)
{
    public async Task<IResult> DeliverAsync(EmailDeliveryRequest input, CancellationToken ct)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.DeliveryId) || input.DeliveryId.Length > 200 ||
            input.DeliveryId.Any(char.IsControl) || input.Message is null)
            return Results.Problem(statusCode: 400, detail: "Invalid delivery request.");
        try { TransactionalEmailSender.Validate(input.Message); }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, detail: "Invalid email content."); }
        if (settings.Provider == EmailProvider.Disabled)
            return Results.Problem(statusCode: 503, detail: "Email delivery is disabled.");

        var id = Hash(input.DeliveryId);
        var message = input.Message with { DeliveryId = null };
        var fingerprint = Hash(JsonSerializer.Serialize(message));
        var claim = await store.ClaimAsync(id, fingerprint, ct);
        if (!claim.Acquired)
        {
            if (claim.Fingerprint != fingerprint)
                return Results.Problem(statusCode: 409, detail: "Delivery ID already belongs to different content.");
            if (claim.State == "ACCEPTED") return Results.Ok(new EmailDeliveryReceipt("ACCEPTED"));
            return Results.Problem(statusCode: 409, detail: "Delivery is pending or unconfirmed. Reconcile before retrying.");
        }

        // Finish recording the attempt even if the HTTP caller disconnects after provider acceptance.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await sender.SendAsync(message, deadline.Token);
            await store.SetStateAsync(id, "ACCEPTED", deadline.Token);
            return Results.Ok(new EmailDeliveryReceipt("ACCEPTED"));
        }
        catch (Exception error) when (error is EmailUnavailableException or OperationCanceledException)
        {
            using var recordDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await store.SetStateAsync(id, "UNCERTAIN", recordDeadline.Token);
            return Results.Problem(statusCode: 503, detail: "Provider acceptance is unconfirmed. Reconcile before retrying.");
        }
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
