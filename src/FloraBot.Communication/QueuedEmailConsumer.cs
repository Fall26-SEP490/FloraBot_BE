using System.Net.Http.Headers;
using System.Net.Http.Json;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Communication;

public sealed class EmailEligibilityClient(HttpClient client, IConfiguration config)
{
    public async Task<bool> EligibleAsync(string id, string fingerprint, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(config["COMMUNICATION_API_URL"]!), "/internal/v1/communication/email-eligibility"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["COMMUNICATION_SERVICE_TOKEN"]);
        request.Content = JsonContent.Create(new EmailEligibilityRequest(id, fingerprint));
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new EmailUnavailableException();
        return (await response.Content.ReadFromJsonAsync<EmailEligibilityResponse>(cancellationToken: ct)
            ?? throw new EmailUnavailableException()).Eligible;
    }
}

public sealed class QueuedEmailConsumer(EmailQueue queue, EmailDeliveryService delivery,
    EmailEligibilityClient eligibility, IConfiguration config, EmailSettings settings, NpgsqlDataSource data) : ICapSubscribe
{
    [CapSubscribe(CommunicationEvents.Requested)]
    public async Task HandleAsync(QueuedEmailDelivery request)
    {
        if (!CommunicationEvents.ValidRequest(request, config)) return;
        if (settings.Provider == EmailProvider.Disabled) throw new EmailUnavailableException();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = deadline.Token;
        var staged = await queue.ReadAsync(request.DeliveryId, ct);
        if (staged.State == "SENDING")
        {
            await using var stale = data.CreateCommand("""
                UPDATE communication.email_deliveries SET state='UNCERTAIN',updated_at=now()
                WHERE id=@id AND state='SENDING' AND updated_at<now()-interval '60 seconds'
                """);
            stale.Parameters.AddWithValue("id", CommunicationEvents.Hash(request.DeliveryId));
            if (await stale.ExecuteNonQueryAsync(ct) == 0) throw new InvalidOperationException("Email is being processed.");
            staged = await queue.ReadAsync(request.DeliveryId, ct);
        }
        if (staged.State is "ACCEPTED" or "UNCERTAIN" or "SKIPPED")
        {
            await queue.CompleteAsync(request.DeliveryId, staged.State, ct);
            return;
        }
        if (staged.Message is null) return; // Terminal skipped requests have already published their result.
        if (!await eligibility.EligibleAsync(request.DeliveryId, staged.Fingerprint, ct))
        {
            await queue.CompleteAsync(request.DeliveryId, "SKIPPED", ct);
            return;
        }
        await delivery.DeliverAsync(new(request.DeliveryId, staged.Message), ct);
        var result = await queue.ReadAsync(request.DeliveryId, ct);
        if (result.State is not ("ACCEPTED" or "UNCERTAIN")) throw new EmailUnavailableException();
        await queue.CompleteAsync(request.DeliveryId, result.State, ct);
    }
}
