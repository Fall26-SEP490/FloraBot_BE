using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace FloraBot.Api.Modules.Notify;

public enum EmailProvider { Disabled, Brevo, GoogleSmtp }

public sealed class EmailSettings
{
    public EmailProvider Provider { get; private init; }
    public string Sender { get; private init; } = "";
    public string Credential { get; private init; } = "";

    public static EmailSettings Load(IConfiguration configuration, bool development)
    {
        var provider = configuration["EMAIL_PROVIDER"] ?? "Disabled";
        if (!Enum.GetNames<EmailProvider>().Contains(provider, StringComparer.OrdinalIgnoreCase) ||
            !Enum.TryParse<EmailProvider>(provider, true, out var selected))
            throw new InvalidOperationException("EMAIL_PROVIDER must be Disabled, Brevo or GoogleSmtp.");
        if (selected == EmailProvider.Disabled) return new();
        var sender = configuration["EMAIL_SENDER"] ?? "";
        if (!IsAddress(sender)) throw new InvalidOperationException("EMAIL_SENDER must be a single email address.");
        if (selected == EmailProvider.GoogleSmtp && !development)
            throw new InvalidOperationException("GoogleSmtp is allowed only in Development.");
        var key = selected == EmailProvider.Brevo ? "BREVO_API_KEY" : "GOOGLE_SMTP_APP_PASSWORD";
        var credential = configuration[key];
        if (string.IsNullOrWhiteSpace(credential) || credential.Any(char.IsControl))
            throw new InvalidOperationException($"{key} is required and must not contain control characters.");
        return new() { Provider = selected, Sender = sender, Credential = credential };
    }

    internal static bool IsAddress(string address) => address.Length is > 0 and <= 254 &&
        !address.Any(char.IsControl) && MailAddress.TryCreate(address, out var parsed) &&
        parsed.DisplayName.Length == 0 && parsed.Address == address && parsed.Host.Contains('.');
}

public sealed record TransactionalEmail(string Recipient, string Subject, string Text);

public sealed class EmailUnavailableException : Exception
{
    public EmailUnavailableException() : base("Transactional email was not confirmed as accepted. Reconcile before retrying.") { }
}

// Acceptance by a provider is not proof of inbox delivery. No automatic send retries here.
public sealed class TransactionalEmailSender(HttpClient client, EmailSettings settings)
{
    public async Task SendAsync(TransactionalEmail email, CancellationToken ct)
    {
        if (!EmailSettings.IsAddress(email.Recipient) || string.IsNullOrWhiteSpace(email.Subject) ||
            email.Subject.Length > 200 || email.Subject.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(email.Text) || Encoding.UTF8.GetByteCount(email.Text) > 32 * 1024)
            throw new ArgumentException("Email requires one recipient, a plain subject and at most 32 KiB of text.", nameof(email));
        if (settings.Provider == EmailProvider.Disabled) throw new EmailUnavailableException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (settings.Provider == EmailProvider.GoogleSmtp)
            {
                using var smtp = new SmtpClient("smtp.gmail.com", 587)
                {
                    EnableSsl = true,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(settings.Sender, settings.Credential)
                };
                using var message = new MailMessage(settings.Sender, email.Recipient, email.Subject, email.Text)
                {
                    IsBodyHtml = false,
                    SubjectEncoding = Encoding.UTF8,
                    BodyEncoding = Encoding.UTF8
                };
                await smtp.SendMailAsync(message, deadline.Token);
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
            request.Headers.Add("api-key", settings.Credential);
            request.Content = JsonContent.Create(new
            {
                sender = new { email = settings.Sender, name = "FloraBot" },
                to = new[] { new { email = email.Recipient } },
                subject = email.Subject,
                textContent = email.Text
            });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.Created || response.Content.Headers.ContentLength > 8192)
                throw new EmailUnavailableException();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (body.Length + count > 8192) throw new EmailUnavailableException();
                body.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(body.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("messageId", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString())) throw new EmailUnavailableException();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or SmtpException or JsonException or OperationCanceledException or IOException)
        {
            // Provider responses/exceptions may contain recipients, credentials or message bodies.
            throw new EmailUnavailableException();
        }
    }
}
