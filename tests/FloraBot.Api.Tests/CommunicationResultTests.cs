using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using DotNetCore.CAP;
using FloraBot.Api.Modules.Identity;

namespace FloraBot.Api.Tests;

public sealed class CommunicationResultTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task DispatchStagesOnceAndWritesOnlyOpaqueRequestsToTransactionalOutbox()
    {
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var data = services.GetRequiredService<NpgsqlDataSource>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["COMMUNICATION_BROKER_ENABLED"] = "true",
            ["EMAIL_PROVIDER"] = "Communication",
            ["COMMUNICATION_SERVICE_URL"] = "http://communication.test",
            ["COMMUNICATION_SERVICE_TOKEN"] = "communication-tests-only-token-at-least-32-characters"
        }).Build();
        var settings = EmailSettings.Load(config, false);
        using var handler = new PreparedHandler();
        using var transport = new HttpClient(handler);
        var dispatch = new CommunicationDispatch(data, services.GetRequiredService<ICapPublisher>(),
            new CommunicationEmailSender(transport, settings), services.GetRequiredService<EmailDelivery>(), settings, config);
        var business = new SellerEmailRequested(Guid.NewGuid(), "approval");
        var content = new SellerEmailContent("approval:" + business.SellerId, "Private subject", "Private body",
            [new(Guid.NewGuid(), "private@example.test")]);
        await dispatch.QueueAsync(business, content, default);
        await dispatch.QueueAsync(business, content, default);
        Assert.Equal(1, handler.Calls);
        await using var query = data.CreateCommand("SELECT count(*) FROM notify.communication_deliveries WHERE batch_key=@batch");
        query.Parameters.AddWithValue("batch", content.DeliveryId);
        Assert.Equal(1L, await query.ExecuteScalarAsync());
        await using var stale = data.CreateCommand("UPDATE notify.communication_deliveries SET updated_at=now()-interval '3 minutes' WHERE batch_key=@batch");
        stale.Parameters.AddWithValue("batch", content.DeliveryId);
        await stale.ExecuteNonQueryAsync();
        await CommunicationRecovery.RecoverAsync(data, services.GetRequiredService<ICapPublisher>(), config, default);
        await using var recovered = data.CreateCommand("SELECT bool_and(updated_at>now()-interval '1 minute') FROM notify.communication_deliveries WHERE batch_key=@batch");
        recovered.Parameters.AddWithValue("batch", content.DeliveryId);
        Assert.Equal(true, await recovered.ExecuteScalarAsync());
    }

    private sealed class PreparedHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new EmailDeliveryReceipt("PREPARED")) });
        }
    }

    [Fact]
    public async Task OutcomesRequireSignatureAndOnlyAdvanceRevision()
    {
        using var client = factory.CreateClient();
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var token = "communication-tests-only-token-at-least-32-characters";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["COMMUNICATION_SERVICE_TOKEN"] = token }).Build();
        var id = Guid.NewGuid().ToString("N");
        await using var insert = data.CreateCommand("""
            INSERT INTO notify.communication_deliveries(delivery_id,batch_key,seller_id,recipient_user_id,kind)
            VALUES(@id,@id,@seller,@recipient,'approval')
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("seller", Guid.NewGuid());
        insert.Parameters.AddWithValue("recipient", Guid.NewGuid());
        await insert.ExecuteNonQueryAsync();
        var subscriber = new CommunicationOutcomes(data, config);
        await subscriber.HandleAsync(new(id, "ACCEPTED", 1, "forged"));
        Assert.Equal("QUEUED", await State());
        await subscriber.HandleAsync(new(id, "ACCEPTED", 2, CommunicationEvents.SignOutcome(id, "ACCEPTED", 2, token)));
        await subscriber.HandleAsync(new(id, "UNCERTAIN", 1, CommunicationEvents.SignOutcome(id, "UNCERTAIN", 1, token)));
        Assert.Equal("ACCEPTED", await State());
        async Task<string> State()
        {
            await using var query = data.CreateCommand("SELECT state FROM notify.communication_deliveries WHERE delivery_id=@id");
            query.Parameters.AddWithValue("id", id);
            return (string)(await query.ExecuteScalarAsync())!;
        }
    }

    [Fact]
    public async Task InternalEligibilityCannotBeCalledWithUserAuthentication()
    {
        using var client = factory.CreateClient();
        var input = new EmailEligibilityRequest("opaque", "fingerprint");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/internal/v1/communication/email-eligibility", input)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/internal/v1/communication/email-eligibility", input)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("SELLER"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/communication/email-deliveries")).StatusCode);
    }
}


