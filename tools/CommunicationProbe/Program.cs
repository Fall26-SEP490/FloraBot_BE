using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Xml.Linq;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using RabbitMQ.Client;

// A canary through the real broker and consumer, without sending a real email.
// The owning API must reject this random ID because no business correlation exists.
if (args.Length is < 1 or > 2 || args.Length == 2 && args[1] != "--broker-ready") return 2;
var phase = "configuration";
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var ct = deadline.Token;
    var config = XDocument.Load(args[0]).Descendants("environmentVariable")
        .ToDictionary(x => (string)x.Attribute("name")!, x => (string)x.Attribute("value")!, StringComparer.OrdinalIgnoreCase);
    string Get(string name) => config.TryGetValue(name, out var value) ? value : throw new InvalidOperationException();
    var token = Get("COMMUNICATION_SERVICE_TOKEN");
    if (token.Length < 32) return 2;
    if (args.Length == 2)
    {
        phase = "service broker consumer readiness";
        var factory = new ConnectionFactory
        {
            HostName = Get("RABBITMQ_HOST"),
            Port = int.Parse(Get("RABBITMQ_PORT")),
            UserName = Get("RABBITMQ_USER"),
            Password = Get("RABBITMQ_PASSWORD"),
            RequestedConnectionTimeout = TimeSpan.FromSeconds(5)
        };
        await using var broker = await factory.CreateConnectionAsync(ct);
        var queue = "florabot.communication." + new NpgsqlConnectionStringBuilder(Get("COMMUNICATION_DATABASE_URL")).Database + ".v1";
        while (true)
        {
            await using var channel = await broker.CreateChannelAsync(cancellationToken: ct);
            try
            {
                var state = await channel.QueueDeclarePassiveAsync(queue, ct);
                if (state.ConsumerCount > 0)
                {
                    Console.WriteLine("PASS: authenticated broker connection and service consumer registered.");
                    return 0;
                }
            }
            catch (RabbitMQ.Client.Exceptions.OperationInterruptedException) { }
            await Task.Delay(400, ct);
        }
    }
    var api = new Uri(Get("COMMUNICATION_API_URL"));
    if (!api.IsLoopback || api.Scheme != "http" || api.Port != 5080) return 2;
    var id = "communication-probe:" + Guid.NewGuid().ToString("N");
    phase = "API eligibility authentication";
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var eligibility = await http.PostAsJsonAsync(new Uri(api, "/internal/v1/communication/email-eligibility"),
        new EmailEligibilityRequest(id, CommunicationEvents.Hash("probe")), ct);
    if (!eligibility.IsSuccessStatusCode || (await eligibility.Content.ReadFromJsonAsync<EmailEligibilityResponse>(ct))?.Eligible != false)
        throw new InvalidOperationException();
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    var connection = Get("COMMUNICATION_DATABASE_URL");
    builder.Services.AddCap(options =>
    {
        // Shorten fallback polling for this bounded canary host, without changing
        // the service configuration or waiting CAP's default four-minute window.
        options.FallbackWindowLookbackSeconds = 1;
        options.FailedRetryInterval = 1;
        options.DefaultGroupName = "florabot.communication.probe." + Guid.NewGuid().ToString("N");
        options.UsePostgreSql(connection);
        options.UseRabbitMQ(rabbit =>
        {
            rabbit.HostName = Get("RABBITMQ_HOST");
            rabbit.Port = int.Parse(Get("RABBITMQ_PORT"));
            rabbit.UserName = Get("RABBITMQ_USER");
            rabbit.Password = Get("RABBITMQ_PASSWORD");
            rabbit.ExchangeName = Get("RABBITMQ_EXCHANGE");
            rabbit.PublishConfirms = true;
        });
    });
    using var host = builder.Build();
    phase = "broker startup";
    await host.StartAsync(ct);
    try
    {
        while (!host.Services.GetRequiredService<IBootstrapper>().IsStarted) await Task.Delay(100, ct);
        phase = "protected message staging";
        using var staged = await http.PostAsJsonAsync("http://127.0.0.1:5090/internal/v1/email-requests",
            new StagedEmailRequest(id, new("communication-probe@example.invalid", "Readiness probe", "Never send: no eligible business request.")), ct);
        if (!staged.IsSuccessStatusCode) throw new InvalidOperationException();
        phase = "broker delivery and consumer eligibility";
        await host.Services.GetRequiredService<ICapPublisher>().PublishAsync(CommunicationEvents.Requested,
            new QueuedEmailDelivery(id, CommunicationEvents.SignRequest(id, token)), cancellationToken: ct);
        await using var data = NpgsqlDataSource.Create(connection);
        while (true)
        {
            await using var query = data.CreateCommand("""
                SELECT q.state, d.id IS NULL FROM communication.email_queue q
                LEFT JOIN communication.email_deliveries d ON d.id=q.id WHERE q.external_id=@id
                """);
            query.Parameters.AddWithValue("id", id);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && reader.GetString(0) == "SKIPPED")
            {
                if (!reader.GetBoolean(1)) throw new InvalidOperationException();
                Console.WriteLine("PASS: broker consumer and API eligibility callback; provider not invoked.");
                return 0;
            }
            await Task.Delay(200, ct);
        }
    }
    finally { await host.StopAsync(CancellationToken.None); }
}
catch (Exception error)
{
    Console.Error.WriteLine($"Communication probe failed during {phase} ({error.GetType().Name}); credentials and provider content suppressed.");
    return 1;
}
