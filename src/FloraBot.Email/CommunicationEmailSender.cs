using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FloraBot.Api.Modules.Notify;

public sealed record EmailDeliveryRequest(string DeliveryId, TransactionalEmail Message);
public sealed record EmailDeliveryReceipt(string State);

public sealed class CommunicationEmailSender(HttpClient client, EmailSettings settings) : ITransactionalEmailSender
{
    public async Task SendAsync(TransactionalEmail email, CancellationToken ct)
    {
        TransactionalEmailSender.Validate(email);
        if (settings.Provider != EmailProvider.Communication || settings.ServiceUrl is null)
            throw new EmailUnavailableException();
        var id = email.DeliveryId ?? Guid.NewGuid().ToString("N");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(settings.ServiceUrl, "/internal/v1/email-deliveries"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Credential);
        request.Content = JsonContent.Create(new EmailDeliveryRequest(id, email with { DeliveryId = null }));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 4096)
                throw new EmailUnavailableException();
            await response.Content.LoadIntoBufferAsync(4096, deadline.Token);
            var receipt = await response.Content.ReadFromJsonAsync<EmailDeliveryReceipt>(cancellationToken: deadline.Token);
            if (receipt?.State != "ACCEPTED") throw new EmailUnavailableException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or JsonException)
        {
            throw new EmailUnavailableException();
        }
    }
}
