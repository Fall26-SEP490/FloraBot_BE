using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Communication.Tests;

public sealed class CommunicationTests(CommunicationFactory factory) : IClassFixture<CommunicationFactory>
{
    private static EmailDeliveryRequest Input() => new(Guid.NewGuid().ToString("N"),
        new TransactionalEmail("recipient@example.test", "Test subject", "Test content"));

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CommunicationFactory.Token);
        return client;
    }

    private static EmailSettings RemoteSettings() => EmailSettings.Load(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EMAIL_PROVIDER"] = "Communication", ["COMMUNICATION_SERVICE_URL"] = "http://localhost",
            ["COMMUNICATION_SERVICE_TOKEN"] = CommunicationFactory.Token
        }).Build(), false);

    [Fact]
    public async Task ServiceHasIndependentHealthAndRequiresInternalAuthentication()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ready")).StatusCode);
        var calls = factory.Provider.Calls;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", Input())).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", Input())).StatusCode);
        Assert.Equal(calls, factory.Provider.Calls);
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var query = data.CreateCommand("SELECT to_regclass('identity.users') IS NULL");
        Assert.Equal(true, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RemoteTransportDeliversOnceAndReceiptSurvivesServiceRestart()
    {
        using var client = factory.CreateClient();
        var sender = new CommunicationEmailSender(client, RemoteSettings());
        var message = Input().Message with { DeliveryId = Guid.NewGuid().ToString("N") };
        var calls = factory.Provider.Calls;
        await sender.SendAsync(message, default);
        await sender.SendAsync(message, default);
        Assert.Equal(calls + 1, factory.Provider.Calls);
        Assert.Equal(message.Recipient, factory.Provider.LastMessage!.Recipient);
        using var restarted = new CommunicationFactory();
        using var restartedClient = restarted.CreateClient();
        await new CommunicationEmailSender(restartedClient, RemoteSettings()).SendAsync(message, default);
        Assert.Equal(0, restarted.Provider.Calls);
        // Only opaque hashes/state are retained, with no address, body or OTP columns.
        await using var query = factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT string_agg(column_name,',' ORDER BY ordinal_position) FROM information_schema.columns
            WHERE table_schema='communication' AND table_name='email_deliveries'
            """);
        Assert.Equal("id,fingerprint,state,created_at,updated_at", await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SimultaneousDuplicateCannotSendAndDifferentContentCannotReuseAnId()
    {
        using var client = Client();
        var input = Input();
        factory.Provider.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Provider.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = factory.Provider.Entered;
        var release = factory.Provider.Continue;
        var first = client.PostAsJsonAsync("/internal/v1/email-deliveries", input);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", input)).StatusCode);
            release.SetResult();
            Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
            var calls = factory.Provider.Calls;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-deliveries",
                input with { Message = input.Message with { Text = "Different content" } })).StatusCode);
            Assert.Equal(calls, factory.Provider.Calls);
            var changed = input with { Message = input.Message with { Text = "Different content" } };
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", changed)).StatusCode);
        }
        finally
        {
            release.TrySetResult();
            await first;
            factory.Provider.Entered = null;
            factory.Provider.Continue = null;
        }
    }

    [Fact]
    public async Task AmbiguousProviderFailureIsRecordedAndDoesNotResendAutomatically()
    {
        using var client = Client();
        var input = Input();
        var calls = factory.Provider.Calls;
        factory.Provider.Fail = true;
        try
        {
            var failed = await client.PostAsJsonAsync("/internal/v1/email-deliveries", input);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            factory.Provider.Fail = false;
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", input)).StatusCode);
            Assert.Equal(calls + 1, factory.Provider.Calls);
            Assert.Contains("UNCERTAIN", await StateAsync(input.DeliveryId));
        }
        finally { factory.Provider.Fail = false; }
    }

    [Fact]
    public async Task InvalidRequestsNeverCallTheProvider()
    {
        using var client = Client();
        var input = Input();
        var calls = factory.Provider.Calls;
        var response = await client.PostAsJsonAsync("/internal/v1/email-deliveries", input with { Message = input.Message with { Recipient = "invalid" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", input with { DeliveryId = "" })).StatusCode);
        Assert.Equal(calls, factory.Provider.Calls);
    }

    [Fact]
    public async Task DisabledProviderIsNotReadyAndDoesNotClaimOrSend()
    {
        using var disabled = factory.WithWebHostBuilder(builder => builder.UseSetting("EMAIL_PROVIDER", "Disabled"));
        using var client = disabled.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CommunicationFactory.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
        var calls = factory.Provider.Calls;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", Input())).StatusCode);
        Assert.Equal(calls, factory.Provider.Calls);
    }

    [Fact]
    public async Task InterruptedAttemptRemainsBlockedAfterRestart()
    {
        var input = Input();
        var id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input.DeliveryId)));
        var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(input.Message))));
        var store = factory.Services.GetRequiredService<DeliveryStore>();
        Assert.True((await store.ClaimAsync(id, fingerprint, default)).Acquired);
        using var restarted = new CommunicationFactory();
        using var client = restarted.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CommunicationFactory.Token);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", input)).StatusCode);
        Assert.Equal(0, restarted.Provider.Calls);
        Assert.Equal("SENDING", await StateAsync(input.DeliveryId));
    }

    [Fact]
    public async Task CallerDisconnectDoesNotLoseAnAcceptedReceipt()
    {
        using var client = Client();
        var input = Input();
        factory.Provider.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Provider.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = factory.Provider.Entered;
        var release = factory.Provider.Continue;
        using var cancel = new CancellationTokenSource();
        var sending = client.PostAsJsonAsync("/internal/v1/email-deliveries", input, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            release.SetResult();
            try { await sending; } catch (OperationCanceledException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await StateAsync(input.DeliveryId) != "ACCEPTED") await Task.Delay(20, timeout.Token);
            var calls = factory.Provider.Calls;
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/internal/v1/email-deliveries", input)).StatusCode);
            Assert.Equal(calls, factory.Provider.Calls);
        }
        finally
        {
            release.TrySetResult();
            factory.Provider.Entered = null;
            factory.Provider.Continue = null;
        }
    }

    [Theory]
    [InlineData(503)]
    [InlineData(401)]
    [InlineData(302)]
    [InlineData(200)]
    public async Task RemoteTransportRejectsUnconfirmedResponsesWithoutAutomaticResend(int status)
    {
        using var handler = new RejectedHandler(status);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<EmailUnavailableException>(() => new CommunicationEmailSender(client, RemoteSettings()).SendAsync(Input().Message, default));
        Assert.Equal(1, handler.Calls);
    }

    private async Task<string> StateAsync(string id)
    {
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var query = data.CreateCommand("SELECT state FROM communication.email_deliveries WHERE id=encode(sha256(convert_to(@id,'UTF8')),'hex')");
        query.Parameters.AddWithValue("id", id);
        return (string)(await query.ExecuteScalarAsync())!;
    }

    private sealed class RejectedHandler(int status) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = JsonContent.Create(new { state = "UNCONFIRMED" }) });
        }
    }
}
