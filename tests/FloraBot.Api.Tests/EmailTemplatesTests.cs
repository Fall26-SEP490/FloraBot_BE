using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class EmailTemplatesTests
{
    [Fact]
    public async Task RegistrationFixtureSelectsOtpForItsRecipientWhenNotificationsInterleave()
    {
        var sender = new TestEmailSender();
        await sender.SendAsync(new("member@example.test", "OTP", "Mã xác thực: 111111"), default);
        await sender.SendAsync(new("another@example.test", "OTP", "Mã xác thực: 222222"), default);
        await sender.SendAsync(new("shop@example.test", "Thông báo", "Shop đã được duyệt."), default);

        Assert.Equal("111111", sender.ExtractLastCode("member@example.test"));
        Assert.Equal("222222", sender.ExtractLastCode("another@example.test"));
        Assert.Null(sender.ExtractLastCode("unknown@example.test"));
    }

    private static EmailSettings Settings(string provider = "Brevo", string sender = "sender@example.test", string credential = "fake-key", bool development = true, string? senderName = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["EMAIL_PROVIDER"] = provider,
            ["EMAIL_SENDER"] = sender,
            ["BREVO_API_KEY"] = credential,
            ["GOOGLE_SMTP_APP_PASSWORD"] = credential
        };
        if (senderName != null) dict["EMAIL_SENDER_NAME"] = senderName;
        return EmailSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(dict).Build(), development);
    }

    [Fact]
    public void CreateDefaultHtmlEncodesUntrustedContent()
    {
        var rawSubject = "<script>alert('xss')</script> Shop & Flora";
        var rawText = "Chào <b onclick=\"alert('1')\">bạn</b>,\n\nBạn có đơn hàng <#123> & \"voucher\".";

        var html = EmailTemplates.CreateDefaultHtml(rawSubject, rawText);

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b onclick", html);
        Assert.Contains("&lt;script&gt;alert(&#39;xss&#39;)&lt;/script&gt;", html);
        Assert.Contains("&lt;b onclick=&quot;alert(&#39;1&#39;)&quot;&gt;bạn&lt;/b&gt;", html);
        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;voucher&quot;", html);
        // Visual theme elements present
        Assert.Contains("#1B4332", html); // Forest green
        Assert.Contains("#F6F8F5", html); // Cream bg
        Assert.Contains("#FFFFFF", html); // White card
        Assert.Contains("🌿 FloraBot", html); // Botanical accent
    }

    [Fact]
    public void RegistrationOtpEncodesUserInputAndPreservesNotice()
    {
        var code = "123456";
        var fullName = "<script>alert('hacked')</script> Nguyễn Văn A";

        var html = EmailTemplates.RegistrationOtp(code, fullName);

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;alert(&#39;hacked&#39;)&lt;/script&gt;", html);
        Assert.Contains("Nguyễn Văn A", html);
        Assert.Contains("123456", html);
        // Expiration and security notice preserved verbatim
        Assert.Contains("Mã có hiệu lực trong 10 phút. Không chia sẻ mã này với bất kỳ ai.", html);
        // Prominent code box styling
        Assert.Contains("letter-spacing: 8px", html);
        Assert.Contains("font-size: 32px", html);
    }

    [Fact]
    public void PasswordResetEncodesValidUrlAndRejectsDangerousSchemes()
    {
        var validUrl = "https://florabot.vn/dat-lai-mat-khau#token=abc%2B123&v=1";
        var maliciousName = "<img src=x onerror=alert(1)> Trần B";

        var html = EmailTemplates.PasswordReset(validUrl, maliciousName);

        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html);
        Assert.Contains("Trần B", html);
        Assert.Contains("href=\"https://florabot.vn/dat-lai-mat-khau#token=abc%2B123&amp;v=1\"", html);
        Assert.Contains("Mở liên kết để đặt lại mật khẩu trong 20 phút. Nếu bạn không yêu cầu, hãy bỏ qua email này.", html);
        Assert.Contains("Đặt lại mật khẩu", html);

        // Dangerous or invalid URLs must be rejected
        Assert.Throws<ArgumentException>(() => EmailTemplates.PasswordReset("javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() => EmailTemplates.PasswordReset("data:text/html,malicious"));
        Assert.Throws<ArgumentException>(() => EmailTemplates.PasswordReset("/relative-url"));
        Assert.Throws<ArgumentException>(() => EmailTemplates.PasswordReset(""));
    }

    [Fact]
    public void SmtpMailMessageBuildsUtf8DisplayNameAndMimeAlternativeViews()
    {
        var settings = Settings("GoogleSmtp", "bot@florabot.vn", "app-pass", development: true, senderName: "FloraBot Việt Nam");
        var email = new TransactionalEmail("user@example.test", "Tiêu đề thử nghiệm", "Nội dung plain text.");

        using var message = TransactionalEmailSender.CreateMailMessage(email, settings);

        Assert.Equal("bot@florabot.vn", message.From!.Address);
        Assert.Equal("FloraBot Việt Nam", message.From.DisplayName);
        Assert.Equal("user@example.test", Assert.Single(message.To).Address);
        Assert.Equal("Tiêu đề thử nghiệm", message.Subject);
        Assert.Equal("Nội dung plain text.", message.Body);
        Assert.False(message.IsBodyHtml);

        // MIME AlternativeViews: text/plain first, text/html second
        Assert.Equal(2, message.AlternateViews.Count);
        Assert.Equal("text/plain", message.AlternateViews[0].ContentType.MediaType);
        Assert.Equal("text/html", message.AlternateViews[1].ContentType.MediaType);

        // Verify alternate view content
        using var plainReader = new StreamReader(message.AlternateViews[0].ContentStream, Encoding.UTF8, leaveOpen: true);
        var plainContent = plainReader.ReadToEnd();
        Assert.Equal("Nội dung plain text.", plainContent);

        using var htmlReader = new StreamReader(message.AlternateViews[1].ContentStream, Encoding.UTF8, leaveOpen: true);
        var htmlContent = htmlReader.ReadToEnd();
        Assert.Contains("Tiêu đề thử nghiệm", htmlContent);
        Assert.Contains("Nội dung plain text.", htmlContent);
        Assert.Contains("#1B4332", htmlContent);
        Assert.Contains("🌿 FloraBot", htmlContent);
    }

    [Fact]
    public void SmtpMailMessagePreservesExplicitHtml()
    {
        var settings = Settings("GoogleSmtp", "bot@florabot.vn", "app-pass", development: true);
        var customHtml = "<html><body><h1>Custom Branded Email</h1></body></html>";
        var email = new TransactionalEmail("user@example.test", "Tiêu đề", "Nội dung", customHtml);

        using var message = TransactionalEmailSender.CreateMailMessage(email, settings);

        Assert.Equal("FloraBot", message.From!.DisplayName);
        using var htmlReader = new StreamReader(message.AlternateViews[1].ContentStream, Encoding.UTF8, leaveOpen: true);
        var htmlContent = htmlReader.ReadToEnd();
        Assert.Equal(customHtml, htmlContent);
    }

    [Fact]
    public async Task BrevoPayloadSendsConfiguredSenderNameAndBothTextAndHtmlContent()
    {
        var capturedSenderName = "";
        var capturedText = "";
        var capturedHtml = "";

        using var handler = new InspectingHandler(reqJson =>
        {
            capturedSenderName = reqJson.GetProperty("sender").GetProperty("name").GetString()!;
            capturedText = reqJson.GetProperty("textContent").GetString()!;
            capturedHtml = reqJson.GetProperty("htmlContent").GetString()!;
        });
        using var client = new HttpClient(handler);
        var settings = Settings("Brevo", "sender@example.test", "api-key", senderName: "FloraBot Chăm Sóc");

        var explicitHtml = EmailTemplates.RegistrationOtp("654321", "Test User");
        var email = new TransactionalEmail("user@example.test", "Mã xác thực", "Mã của bạn: 654321", explicitHtml);

        await new TransactionalEmailSender(client, settings).SendAsync(email, default);

        Assert.Equal("FloraBot Chăm Sóc", capturedSenderName);
        Assert.Equal("Mã của bạn: 654321", capturedText);
        Assert.Equal(explicitHtml, capturedHtml);
        Assert.Contains("654321", capturedHtml);
    }

    [Fact]
    public async Task BrevoPayloadGeneratesBrandedLayoutWhenHtmlNotSupplied()
    {
        var capturedHtml = "";
        using var handler = new InspectingHandler(reqJson =>
        {
            capturedHtml = reqJson.GetProperty("htmlContent").GetString()!;
        });
        using var client = new HttpClient(handler);
        var settings = Settings("Brevo", "sender@example.test", "api-key");
        var email = new TransactionalEmail("user@example.test", "Thông báo", "Đơn hàng đã được duyệt.");

        await new TransactionalEmailSender(client, settings).SendAsync(email, default);

        Assert.False(string.IsNullOrWhiteSpace(capturedHtml));
        Assert.Contains("Thông báo", capturedHtml);
        Assert.Contains("Đơn hàng đã được duyệt.", capturedHtml);
        Assert.Contains("🌿 FloraBot", capturedHtml);
        Assert.Contains("#1B4332", capturedHtml);
    }

    [Theory]
    [InlineData("FloraBot\r\nBcc: evil@example.test")]
    [InlineData("FloraBot\r\n")]
    [InlineData("FloraBot\n")]
    [InlineData("FloraBot\r")]
    [InlineData("FloraBot\0evil")]
    [InlineData("   ")]
    public void SenderNameWithControlCharsOrWhitespaceThrows(string invalidName)
    {
        Assert.Throws<InvalidOperationException>(() => Settings(senderName: invalidName));
    }

    [Fact]
    public void SenderNameExceeding100CharactersThrows()
    {
        var longName = new string('X', 101);
        Assert.Throws<InvalidOperationException>(() => Settings(senderName: longName));
    }

    [Fact]
    public void TransactionalEmailBackwardsCompatibilityPreserved()
    {
        // 3-argument constructor
        var threeArg = new TransactionalEmail("a@b.c", "Subj", "Text");
        Assert.Equal("a@b.c", threeArg.Recipient);
        Assert.Equal("Subj", threeArg.Subject);
        Assert.Equal("Text", threeArg.Text);
        Assert.Null(threeArg.Html);

        // 4-argument constructor
        var fourArg = new TransactionalEmail("a@b.c", "Subj", "Text", "<p>Html</p>");
        Assert.Equal("<p>Html</p>", fourArg.Html);

        // Record with-expression
        var updated = threeArg with { Html = "<b>Updated</b>" };
        Assert.Equal("<b>Updated</b>", updated.Html);
        Assert.Equal("Subj", updated.Subject);
    }

    [Fact]
    public void AuthEmailTemplatesProduceValidBrandedMessages()
    {
        var code = "123456";
        var regHtml = EmailTemplates.RegistrationOtp(code, "Lê Thị C");
        var regEmail = new TransactionalEmail(
            "customer@example.test",
            "Mã xác thực đăng ký tài khoản FloraBot",
            $"Mã xác thực của bạn là: {code}\nMã có hiệu lực trong 10 phút. Không chia sẻ mã này với bất kỳ ai.",
            regHtml);

        Assert.Equal("customer@example.test", regEmail.Recipient);
        Assert.Equal("Mã xác thực đăng ký tài khoản FloraBot", regEmail.Subject);
        Assert.Contains(code, regEmail.Text);
        Assert.Contains(code, regEmail.Html);
        Assert.Contains("Lê Thị C", regEmail.Html);
        Assert.Contains("10 phút", regEmail.Html);

        var resetUrl = "https://florabot.vn/dat-lai-mat-khau#token=xyz123";
        var resetHtml = EmailTemplates.PasswordReset(resetUrl, "Lê Thị C");
        var resetEmail = new TransactionalEmail(
            "customer@example.test",
            "Đặt lại mật khẩu FloraBot",
            $"Mở liên kết để đặt lại mật khẩu trong 20 phút: {resetUrl}\nNếu bạn không yêu cầu, hãy bỏ qua email này.",
            resetHtml);

        Assert.Equal("customer@example.test", resetEmail.Recipient);
        Assert.Equal("Đặt lại mật khẩu FloraBot", resetEmail.Subject);
        Assert.Contains(resetUrl, resetEmail.Text);
        Assert.Contains(resetUrl, resetEmail.Html);
        Assert.Contains("20 phút", resetEmail.Html);
        Assert.Contains("Lê Thị C", resetEmail.Html);
    }

    private sealed class InspectingHandler(Action<JsonElement> inspector) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            inspector(json.RootElement);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"messageId\":\"msg-test-123\"}")
            };
        }
    }
}
