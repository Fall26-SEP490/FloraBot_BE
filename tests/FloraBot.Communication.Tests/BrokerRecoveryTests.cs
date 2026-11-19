using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace FloraBot.Communication.Tests;

public sealed class BrokerRecoveryTests
{
    [Fact]
    public async Task DurableRequestSurvivesBrokerDisconnectAndServiceRestartWithoutDuplicateSend()
    {
        await using var proxy = new BrokerFaultProxy(Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost", 55672);
        var keys = Path.Combine(Path.GetTempPath(), "florabot-communication-tests", Guid.NewGuid().ToString("N"));
        var exchange = "florabot.recovery." + Guid.NewGuid().ToString("N");
        using var firstBase = new CommunicationFactory();
        using var first = Create(firstBase);
        using var firstClient = Client(first);
        var publisher = first.Services.GetRequiredService<ICapPublisher>();
        var data = first.Services.GetRequiredService<NpgsqlDataSource>();
        var warmup = Guid.NewGuid().ToString("N");
        await Stage(firstClient, warmup);
        await Wait(async () =>
        {
            await publisher.PublishAsync(CommunicationEvents.Requested, Request(warmup));
            return await State(data, warmup) == "ACCEPTED";
        });
        Assert.Equal(1, firstBase.Provider.Calls);

        var pending = Guid.NewGuid().ToString("N");
        await Stage(firstClient, pending);
        proxy.SetOnline(false);
        await publisher.PublishAsync(CommunicationEvents.Requested, Request(pending));
        await using (var query = data.CreateCommand("SELECT count(*) FROM cap.published WHERE \"Content\" LIKE @id"))
        {
            query.Parameters.AddWithValue("id", "%" + pending + "%");
            Assert.True((long)(await query.ExecuteScalarAsync())! > 0);
        }
        Assert.Equal("PREPARED", await State(data, pending));
        await first.DisposeAsync();

        proxy.SetOnline(true);
        using var secondBase = new CommunicationFactory();
        using var second = Create(secondBase);
        using var secondClient = Client(second);
        var secondData = second.Services.GetRequiredService<NpgsqlDataSource>();
        // No manual re-publication: CAP must replay the persisted request after the process restart.
        await Wait(async () => await State(secondData, pending) == "ACCEPTED");
        Assert.Equal(1, secondBase.Provider.Calls);
        await using (var scope = second.Services.CreateAsyncScope())
        {
            var consumer = scope.ServiceProvider.GetRequiredService<QueuedEmailConsumer>();
            await consumer.HandleAsync(Request(pending));
            await consumer.HandleAsync(Request(warmup));
        }
        Assert.Equal(1, secondBase.Provider.Calls);
        Assert.Equal(1, firstBase.Provider.Calls);

        WebApplicationFactory<CommunicationProgram> Create(CommunicationFactory source) => source.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("COMMUNICATION_BROKER_ENABLED", "true");
            builder.UseSetting("COMMUNICATION_API_URL", "http://core.test");
            builder.UseSetting("DATA_PROTECTION_KEYS_PATH", keys);
            builder.UseSetting("RABBITMQ_HOST", "127.0.0.1");
            builder.UseSetting("RABBITMQ_PORT", proxy.Port.ToString());
            builder.UseSetting("RABBITMQ_EXCHANGE", exchange);
            builder.UseSetting("RABBITMQ_PASSWORD", "local-florabot-only");
            builder.ConfigureServices(services =>
            {
                services.Configure<CapOptions>(options => { options.FailedRetryInterval = 1; options.FallbackWindowLookbackSeconds = 1; });
                services.AddHttpClient<EmailEligibilityClient>().ConfigurePrimaryHttpMessageHandler(() => new Eligible());
            });
        });
    }

    private static HttpClient Client(WebApplicationFactory<CommunicationProgram> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CommunicationFactory.Token);
        return client;
    }
    private static async Task Stage(HttpClient client, string id) => Assert.Equal(HttpStatusCode.OK,
        (await client.PostAsJsonAsync("/internal/v1/email-requests", new StagedEmailRequest(id,
            new("recovery@example.test", "Recovery", "Private recovery body")))).StatusCode);
    private static QueuedEmailDelivery Request(string id) => new(id, CommunicationEvents.SignRequest(id, CommunicationFactory.Token));
    private static async Task<string?> State(NpgsqlDataSource data, string id)
    {
        await using var query = data.CreateCommand("SELECT state FROM communication.email_queue WHERE external_id=@id");
        query.Parameters.AddWithValue("id", id);
        return await query.ExecuteScalarAsync() as string;
    }
    private static async Task Wait(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!await predicate()) await Task.Delay(300, deadline.Token);
    }
    private sealed class Eligible : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new EmailEligibilityResponse(true)) });
    }
}

