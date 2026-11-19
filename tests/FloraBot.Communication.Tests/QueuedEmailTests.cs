using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Communication.Tests;

public sealed class QueuedEmailTests
{
    [Fact]
    public async Task BrokerDeliveryRevalidatesAndPersistsOutcomeWithoutPlaintext()
    {
        using var baseFactory = new CommunicationFactory();
        var keys = Path.Combine(Path.GetTempPath(), "florabot-communication-tests", Guid.NewGuid().ToString("N"));
        var eligibility = new EligibilityHandler();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("COMMUNICATION_BROKER_ENABLED", "true");
            builder.UseSetting("COMMUNICATION_API_URL", "http://core.test");
            builder.UseSetting("DATA_PROTECTION_KEYS_PATH", keys);
            builder.UseSetting("RABBITMQ_EXCHANGE", "florabot.communication.tests");
            builder.UseSetting("RABBITMQ_PASSWORD", "local-florabot-only");
            builder.ConfigureServices(services => services.AddHttpClient<EmailEligibilityClient>()
                .ConfigurePrimaryHttpMessageHandler(() => eligibility));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CommunicationFactory.Token);
        var id = Guid.NewGuid().ToString("N");
        var message = new TransactionalEmail("queued@example.test", "Queued subject", "Private body");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/internal/v1/email-requests", new StagedEmailRequest(id, message))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-requests", new StagedEmailRequest(id, message with { Text = "Changed" }))).StatusCode);
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var query = data.CreateCommand("SELECT protected_payload FROM communication.email_queue WHERE external_id=@id"))
        {
            query.Parameters.AddWithValue("id", id);
            var payload = Assert.IsType<string>(await query.ExecuteScalarAsync());
            Assert.DoesNotContain(message.Recipient, payload);
            Assert.DoesNotContain(message.Text, payload);
        }
        var publisher = factory.Services.GetRequiredService<ICapPublisher>();
        await publisher.PublishAsync(CommunicationEvents.Requested, new QueuedEmailDelivery(id, "forged"));
        // CAP's subscription startup is asynchronous; its durable queue must exist before the first request.
        await WaitAsync(async () =>
        {
            await using var query = data.CreateCommand("SELECT count(*) FROM cap.received WHERE \"Name\"=@name");
            query.Parameters.AddWithValue("name", CommunicationEvents.Requested);
            await publisher.PublishAsync(CommunicationEvents.Requested, new QueuedEmailDelivery(id, "forged"));
            return (long)(await query.ExecuteScalarAsync())! > 0;
        });
        Assert.Equal(0, baseFactory.Provider.Calls);
        var signed = new QueuedEmailDelivery(id, CommunicationEvents.SignRequest(id, CommunicationFactory.Token));
        await publisher.PublishAsync(CommunicationEvents.Requested, signed);
        await WaitAsync(async () => await StateAsync(data, id) == "ACCEPTED");
        Assert.Equal(1, baseFactory.Provider.Calls);
        Assert.True(eligibility.Calls > 0);
        await publisher.PublishAsync(CommunicationEvents.Requested, signed);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<QueuedEmailConsumer>().HandleAsync(signed);
        Assert.Equal(1, baseFactory.Provider.Calls);
        await using (var query = data.CreateCommand("SELECT protected_payload IS NULL AND revision=1 FROM communication.email_queue WHERE external_id=@id"))
        {
            query.Parameters.AddWithValue("id", id);
            Assert.Equal(true, await query.ExecuteScalarAsync());
        }
        eligibility.Eligible = false;
        var skipped = Guid.NewGuid().ToString("N");
        await client.PostAsJsonAsync("/internal/v1/email-requests", new StagedEmailRequest(skipped, message));
        await publisher.PublishAsync(CommunicationEvents.Requested, new QueuedEmailDelivery(skipped, CommunicationEvents.SignRequest(skipped, CommunicationFactory.Token)));
        await WaitAsync(async () => await StateAsync(data, skipped) == "SKIPPED");
        Assert.Equal(1, baseFactory.Provider.Calls);

        eligibility.Eligible = true;
        baseFactory.Provider.Fail = true;
        var uncertain = Guid.NewGuid().ToString("N");
        await client.PostAsJsonAsync("/internal/v1/email-requests", new StagedEmailRequest(uncertain, message));
        var uncertainEvent = new QueuedEmailDelivery(uncertain, CommunicationEvents.SignRequest(uncertain, CommunicationFactory.Token));
        await publisher.PublishAsync(CommunicationEvents.Requested, uncertainEvent);
        await WaitAsync(async () => await StateAsync(data, uncertain) == "UNCERTAIN");
        Assert.Equal(2, baseFactory.Provider.Calls);
        var reconcile = new ReconcileEmailRequest(Guid.NewGuid(), uncertain, "UNCERTAIN", 1, "ACCEPTED", "provider-test-reference", "Provider confirmed acceptance", Guid.NewGuid());
        var response = await client.PostAsJsonAsync("/internal/v1/email-requests/reconcile", reconcile);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/internal/v1/email-requests/reconcile", reconcile)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-requests/reconcile", reconcile with { Reason = "Different operation content" })).StatusCode);
        Assert.Equal("ACCEPTED", await StateAsync(data, uncertain));
        Assert.Equal(2, baseFactory.Provider.Calls);

        var retryId = Guid.NewGuid().ToString("N");
        await client.PostAsJsonAsync("/internal/v1/email-requests", new StagedEmailRequest(retryId, message));
        await publisher.PublishAsync(CommunicationEvents.Requested, new QueuedEmailDelivery(retryId, CommunicationEvents.SignRequest(retryId, CommunicationFactory.Token)));
        await WaitAsync(async () => await StateAsync(data, retryId) == "UNCERTAIN");
        baseFactory.Provider.Fail = false;
        var retry = new ReconcileEmailRequest(Guid.NewGuid(), retryId, "UNCERTAIN", 1,
            "RETRY_CONFIRMED_NOT_SENT", "provider-test-reference", "Provider confirmed no acceptance", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/internal/v1/email-requests/reconcile", retry)).StatusCode);
        await WaitAsync(async () => await StateAsync(data, retryId) == "ACCEPTED");
        Assert.Equal(4, baseFactory.Provider.Calls);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/internal/v1/email-requests/reconcile", retry with { OperationId = Guid.NewGuid() })).StatusCode);
    }

    private static async Task<string?> StateAsync(NpgsqlDataSource data, string id)
    {
        await using var query = data.CreateCommand("SELECT state FROM communication.email_queue WHERE external_id=@id");
        query.Parameters.AddWithValue("id", id);
        return await query.ExecuteScalarAsync() as string;
    }

    private static async Task WaitAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (!await predicate()) await Task.Delay(200, deadline.Token);
    }

    private sealed class EligibilityHandler : HttpMessageHandler
    {
        public bool Eligible = true;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal("Bearer " + CommunicationFactory.Token, request.Headers.Authorization!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new EmailEligibilityResponse(Eligible)) });
        }
    }
}
