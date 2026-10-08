using System.Net;
using System.Text.Json;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class TransactionalEmailTests
{
    private static readonly TransactionalEmail Message = new("shop@example.test", "FloraBot: shop đã được duyệt", "Chào bạn, shop đã được duyệt. Thanh toán gói là bước riêng.");
    private static EmailSettings Settings(string provider = "Brevo", string sender = "sender@example.test", string credential = "fake-key", bool development = true) =>
        EmailSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EMAIL_PROVIDER"] = provider,
            ["EMAIL_SENDER"] = sender,
            ["BREVO_API_KEY"] = credential,
            ["GOOGLE_SMTP_APP_PASSWORD"] = credential
        }).Build(), development);

    [Fact]
    public async Task BrevoUsesSingleRecipientPlainTextAndRequiresAcceptance()
    {
        using var handler = new Provider();
        using var client = new HttpClient(handler);
        await new TransactionalEmailSender(client, Settings()).SendAsync(Message, default);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(400, "{\"error\":\"secret recipient\"}")]
    [InlineData(429, "limit")]
    [InlineData(503, "unavailable")]
    [InlineData(302, "redirect")]
    [InlineData(200, "{\"messageId\":\"id\"}")]
    [InlineData(201, "{}")]
    [InlineData(201, "{\"messageId\":null}")]
    [InlineData(201, "{\"messageId\":\" \"}")]
    [InlineData(201, "[]")]
    [InlineData(201, "bad json")]
    public async Task RejectedOrAmbiguousResponsesNeverRetryOrExposeProviderBody(int status, string body)
    {
        using var handler = new Provider(status, body);
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<EmailUnavailableException>(() => new TransactionalEmailSender(client, Settings()).SendAsync(Message, default));
        Assert.Equal(1, handler.Calls);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(body, exception.Message);
    }

    [Fact]
    public async Task BoundsResponseAndSanitizesNetworkFailures()
    {
        foreach (var mode in new[] { "large", "network", "timeout" })
        {
            using var handler = new Provider(mode: mode);
            using var client = new HttpClient(handler);
            var error = await Assert.ThrowsAsync<EmailUnavailableException>(() => new TransactionalEmailSender(client, Settings()).SendAsync(Message, default));
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("sensitive", error.ToString());
            Assert.Equal(1, handler.Calls);
        }
    }

    [Fact]
    public async Task InvalidInputAndDisabledDeliveryNeverCallProvider()
    {
        using var handler = new Provider();
        using var client = new HttpClient(handler);
        var sender = new TransactionalEmailSender(client, Settings());
        foreach (var message in new[]
        {
            Message with { Recipient = "one@example.test,two@example.test" },
            Message with { Recipient = "Shop <one@example.test>" },
            Message with { Recipient = "one@example.test\r\nBcc: other@example.test" },
            Message with { Subject = "Subject\r\nBcc: other@example.test" },
            Message with { Subject = new string('a', 201) },
            Message with { Text = "" },
            Message with { Text = new string('ớ', 12000) }
        }) await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync(message, default));
        await Assert.ThrowsAsync<EmailUnavailableException>(() => new TransactionalEmailSender(client, Settings("Disabled", "", "")).SendAsync(Message, default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("unknown", "sender@example.test", "key", true)]
    [InlineData("1", "sender@example.test", "key", true)]
    [InlineData("Brevo", "", "key", true)]
    [InlineData("Brevo", "Name <sender@example.test>", "key", true)]
    [InlineData("Brevo", "sender@example.test", "", true)]
    [InlineData("Brevo", "sender@example.test", "key\r\ninjection", true)]
    [InlineData("GoogleSmtp", "sender@example.test", "key", false)]
    public void InvalidEnabledConfigurationFailsImmediately(string provider, string sender, string credential, bool development) =>
        Assert.Throws<InvalidOperationException>(() => Settings(provider, sender, credential, development));

    [Fact]
    public void DefaultIsDisabledAndGoogleSmtpIsExplicitDevelopmentOption()
    {
        Assert.Equal(EmailProvider.Disabled, EmailSettings.Load(new ConfigurationBuilder().Build(), false).Provider);
        Assert.Equal(EmailProvider.GoogleSmtp, Settings("GoogleSmtp").Provider);
    }

    [Fact]
    public async Task CallerCancellationIsPreserved()
    {
        using var handler = new Provider(mode: "cancel");
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TransactionalEmailSender(client, Settings()).SendAsync(Message, cancellation.Token));
    }

    private sealed class Provider(int status = 201, string body = "{\"messageId\":\"provider-id\"}", string mode = "ok") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.brevo.com/v3/smtp/email", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fake-key", Assert.Single(request.Headers.GetValues("api-key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("sender@example.test", json.RootElement.GetProperty("sender").GetProperty("email").GetString());
            Assert.Equal(Message.Recipient, Assert.Single(json.RootElement.GetProperty("to").EnumerateArray()).GetProperty("email").GetString());
            Assert.Equal(Message.Subject, json.RootElement.GetProperty("subject").GetString());
            Assert.Equal(Message.Text, json.RootElement.GetProperty("textContent").GetString());
            Assert.False(json.RootElement.TryGetProperty("htmlContent", out _));
            if (mode == "network") throw new HttpRequestException("sensitive");
            if (mode == "timeout") throw new OperationCanceledException("sensitive");
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = mode == "large" ? new StreamContent(new MemoryStream(new byte[9000])) : new StringContent(body)
            };
        }
    }
}
