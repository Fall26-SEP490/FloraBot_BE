using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using StackExchange.Redis;

namespace FloraBot.Api.Tests;

public sealed class SellerEmailTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();
    private IConnectionMultiplexer Redis => factory.Services.GetRequiredService<IConnectionMultiplexer>();
    private static EmailSettings Settings(string provider = "Brevo") => EmailSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["EMAIL_PROVIDER"] = provider, ["EMAIL_SENDER"] = "sender@example.test", ["BREVO_API_KEY"] = "fake" }).Build(), false);

    private async Task<(Guid Seller, Guid User)> FixtureAsync(bool email = true, string status = "APPROVED")
    {
        var seller = Guid.NewGuid(); var user = Guid.NewGuid();
        await using var command = Data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
            VALUES(@seller,'Tiệm Hoa Thử','0902223344',@status,'20000000-0000-0000-0000-00000000000a');
            INSERT INTO identity.users(id,email,phone,password_hash,full_name,role,seller_id)
            VALUES(@user,CASE WHEN @email THEN @user::text || '@example.test' ELSE NULL END,@user::text,'!unprovisioned','Chủ shop','SELLER',@seller);
            """);
        command.Parameters.AddWithValue("seller", seller); command.Parameters.AddWithValue("user", user);
        command.Parameters.AddWithValue("email", email); command.Parameters.AddWithValue("status", status);
        await command.ExecuteNonQueryAsync();
        return (seller, user);
    }

    [Fact]
    public async Task ApprovalEndpointPublishesOnlyIdsOnceAndUnauthorizedRequestDoesNotPublish()
    {
        var fixture = await FixtureAsync(status: "PENDING");
        using var client = factory.CreateClient();
        var input = new { p_seller = fixture.Seller, p_package = Guid.Parse("20000000-0000-0000-0000-00000000000a") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/flows/approve_seller", input)).StatusCode);
        Assert.Empty(await EventsAsync(fixture.Seller));
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/admin/flows/approve_seller", input)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/flows/approve_seller", input)).StatusCode);
        var payload = Assert.Single(await EventsAsync(fixture.Seller));
        Assert.Equal(SellerEmailEvents.Approval, payload.GetProperty("kind").GetString());
        Assert.Equal(fixture.Seller, payload.GetProperty("seller_id").GetGuid());
        Assert.DoesNotContain("@", payload.GetRawText());
        Assert.DoesNotContain("Tiệm", payload.GetRawText());
    }

    [Fact]
    public async Task RolledBackApprovalDoesNotPersistEmailOrSellerApproval()
    {
        var fixture = await FixtureAsync(status: "PENDING");
        using var scope = factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<ICapPublisher>();
        await using var connection = await Data.OpenConnectionAsync();
        using var outbox = await connection.BeginTransactionAsync(publisher);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, default);
        await using var command = new NpgsqlCommand("SELECT flow.approve_seller(@seller,'10000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-00000000000a')", connection, transaction);
        command.Parameters.AddWithValue("seller", fixture.Seller);
        await command.ExecuteNonQueryAsync();
        await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, default);
        await outbox.RollbackAsync();
        Assert.Empty(await EventsAsync(fixture.Seller));
        await using var check = Data.CreateCommand("SELECT status FROM identity.sellers WHERE id=@id");
        check.Parameters.AddWithValue("id", fixture.Seller);
        Assert.Equal("PENDING", await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RecipientLookupExcludesOtherShopAndInactiveAccountsAndCanRecoverAfterProvisioning()
    {
        var fixture = await FixtureAsync(email: false);
        var other = await FixtureAsync();
        var reader = new SellerEmailReader(Data);
        var message = new SellerEmailRequested(fixture.Seller, SellerEmailEvents.Approval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(message, default));
        await using var update = Data.CreateCommand("UPDATE identity.users SET email=@email WHERE id=@id");
        update.Parameters.AddWithValue("email", fixture.User + "@example.test"); update.Parameters.AddWithValue("id", fixture.User);
        await update.ExecuteNonQueryAsync();
        var content = await reader.ReadAsync(message, default);
        Assert.NotNull(content);
        Assert.Equal(fixture.User, Assert.Single(content.Recipients).UserId);
        Assert.DoesNotContain(other.User.ToString(), content.Text);
        update.CommandText = "UPDATE identity.users SET status='LOCKED' WHERE id=@id";
        await update.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(message, default));
        update.CommandText = "UPDATE identity.sellers SET status='CLOSED' WHERE id=@seller";
        update.Parameters.AddWithValue("seller", fixture.Seller);
        await update.ExecuteNonQueryAsync();
        Assert.Null(await reader.ReadAsync(message, default));
    }

    [Fact]
    public async Task PaidSubscriptionUsesSourceAmountAndPeriodAndRejectsForeignSeller()
    {
        var fixture = await FixtureAsync();
        await using var command = Data.CreateCommand("SELECT flow.subscribe(@seller,@user,1)");
        command.Parameters.AddWithValue("seller", fixture.Seller); command.Parameters.AddWithValue("user", fixture.User);
        var subscription = (Guid)(await command.ExecuteScalarAsync())!;
        var reader = new SellerEmailReader(Data);
        var message = new SellerEmailRequested(fixture.Seller, SellerEmailEvents.Receipt, subscription);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(message, default));
        command.CommandText = "SELECT flow.subscription_paid(@subscription,@txn)";
        command.Parameters.AddWithValue("subscription", subscription); command.Parameters.AddWithValue("txn", Guid.NewGuid().ToString());
        await command.ExecuteNonQueryAsync();
        var content = await reader.ReadAsync(message, default);
        Assert.NotNull(content);
        Assert.Contains("400.000 đ", content.Text);
        Assert.Contains(subscription.ToString(), content.Text);
        Assert.Contains("đến trước ngày", content.Text);
        var other = await FixtureAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(message with { SellerId = other.Seller }, default));
    }

    [Fact]
    public async Task SubscriberDeduplicatesRecipientsAndDoesNotMarkFailedDeliveryAccepted()
    {
        var fixture = await FixtureAsync();
        var settings = Settings();
        using var handler = new Provider();
        using var http = new HttpClient(handler);
        var delivery = new EmailDelivery(Redis, Data, new TransactionalEmailSender(http, settings));
        var subscriber = new SellerEmailSubscriber(settings, new SellerEmailReader(Data), delivery, NullLogger<SellerEmailSubscriber>.Instance);
        var message = new SellerEmailRequested(fixture.Seller, SellerEmailEvents.Approval);
        handler.Fail = true;
        await Assert.ThrowsAsync<EmailUnavailableException>(() => subscriber.HandleAsync(message));
        var deliveryId = $"approval:{fixture.Seller}:{fixture.User}";
        Assert.False(await Redis.GetDatabase().KeyExistsAsync(delivery.Key(deliveryId) + ":accepted"));
        handler.Fail = false;
        await subscriber.HandleAsync(message);
        await subscriber.HandleAsync(message);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(fixture.User + "@example.test", handler.Recipient);
        Assert.True(await Redis.GetDatabase().KeyExistsAsync(delivery.Key(deliveryId) + ":accepted"));
        var disabled = new SellerEmailSubscriber(Settings("Disabled"), new SellerEmailReader(Data), delivery, NullLogger<SellerEmailSubscriber>.Instance);
        await disabled.HandleAsync(message with { SellerId = Guid.NewGuid() });
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task HeldDeliveryLeasePreventsConcurrentProviderAttempt()
    {
        using var handler = new Provider();
        using var http = new HttpClient(handler);
        var delivery = new EmailDelivery(Redis, Data, new TransactionalEmailSender(http, Settings()));
        var id = Guid.NewGuid().ToString();
        var key = delivery.Key(id) + ":lock";
        await Redis.GetDatabase().StringSetAsync(key, "another-worker", TimeSpan.FromMinutes(1));
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => delivery.SendOnceAsync(id, new("one@example.test", "Subject", "Text"), default));
            Assert.Equal(0, handler.Calls);
            Assert.Equal("another-worker", (string?)await Redis.GetDatabase().StringGetAsync(key));
        }
        finally { await Redis.GetDatabase().KeyDeleteAsync(key); }
    }

    [Fact]
    public async Task RemindersUseServerDateCommitWithAuditAndSuppressStaleExpiry()
    {
        var fixture = await FixtureAsync();
        await using var update = Data.CreateCommand("""
            UPDATE identity.sellers SET status='ACTIVE',
              package_expires_at=(CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date+2
            WHERE id=@id RETURNING package_expires_at
            """);
        update.Parameters.AddWithValue("id", fixture.Seller);
        var expiry = (DateOnly)(await update.ExecuteScalarAsync())!;
        var excluded = new List<Guid>();
        foreach (var days in new[] { -1, 0, 4 })
        {
            var other = await FixtureAsync(); excluded.Add(other.Seller);
            await using var age = Data.CreateCommand("UPDATE identity.sellers SET status='ACTIVE',package_expires_at=(CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date+@days WHERE id=@id");
            age.Parameters.AddWithValue("id", other.Seller); age.Parameters.AddWithValue("days", days);
            await age.ExecuteNonQueryAsync();
        }
        using var scope = factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetRequiredService<ICapPublisher>();
        async Task Enqueue(bool commit)
        {
            await using var connection = await Data.OpenConnectionAsync();
            using var outbox = await connection.BeginTransactionAsync(publisher);
            await SubscriptionReminders.EnqueueAsync(connection, (NpgsqlTransaction)outbox.DbTransaction!, publisher, default);
            if (commit) await outbox.CommitAsync(); else await outbox.RollbackAsync();
        }
        await Enqueue(false);
        Assert.Empty(await EventsAsync(fixture.Seller));
        await Enqueue(true);
        await Enqueue(true);
        var item = Assert.Single(await EventsAsync(fixture.Seller));
        Assert.Equal(SellerEmailEvents.Reminder, item.GetProperty("kind").GetString());
        Assert.Equal(expiry, DateOnly.Parse(item.GetProperty("expires_on").GetString()!));
        foreach (var id in excluded) Assert.Empty(await EventsAsync(id));
        await using var audit = Data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='SUBSCRIPTION_REMINDER_QUEUED' AND entity_id=@id");
        audit.Parameters.AddWithValue("id", fixture.Seller);
        Assert.Equal(1L, await audit.ExecuteScalarAsync());
        var reader = new SellerEmailReader(Data);
        var message = new SellerEmailRequested(fixture.Seller, SellerEmailEvents.Reminder, ExpiresOn: expiry);
        Assert.Contains("sắp hết hạn", (await reader.ReadAsync(message, default))!.Subject);
        update.CommandText = "UPDATE identity.sellers SET package_expires_at=package_expires_at+30 WHERE id=@id";
        await update.ExecuteNonQueryAsync();
        Assert.Null(await reader.ReadAsync(message, default));
        update.CommandText = "UPDATE identity.sellers SET package_expires_at=(CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date+3 WHERE id=@id RETURNING package_expires_at";
        var nextExpiry = (DateOnly)(await update.ExecuteScalarAsync())!;
        await Enqueue(true);
        Assert.Equal(2, (await EventsAsync(fixture.Seller)).Count);
        update.CommandText = "UPDATE identity.sellers SET status='SUSPENDED' WHERE id=@id";
        await update.ExecuteNonQueryAsync();
        Assert.Null(await reader.ReadAsync(message with { ExpiresOn = nextExpiry }, default));
    }

    private async Task<List<JsonElement>> EventsAsync(Guid seller)
    {
        await using var query = Data.CreateCommand("""
            SELECT "Content"::jsonb->'Value' FROM cap.published
            WHERE "Name"=@name AND "Content"::jsonb->'Value'->>'seller_id'=@id
            """);
        query.Parameters.AddWithValue("name", SellerEmailEvents.Requested); query.Parameters.AddWithValue("id", seller.ToString());
        await using var reader = await query.ExecuteReaderAsync();
        var result = new List<JsonElement>();
        while (await reader.ReadAsync()) result.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return result;
    }

    [Fact]
    public async Task CommittedApprovalTravelsThroughBrokerToEnabledSubscriberWithFakeProvider()
    {
        var fixture = await FixtureAsync(status: "PENDING");
        var schema = "cap_email_" + Guid.NewGuid().ToString("N");
        using var handler = new Provider();
        using var isolated = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("EMAIL_PROVIDER", "Brevo");
            builder.UseSetting("EMAIL_SENDER", "sender@example.test");
            builder.UseSetting("BREVO_API_KEY", "fake");
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<PostgreSqlOptions>(options => options.Schema = schema);
                services.PostConfigure<CapOptions>(options => options.DefaultGroupName = schema);
                services.PostConfigure<RabbitMQOptions>(options => options.ExchangeName = schema);
                services.AddHttpClient<TransactionalEmailSender>().ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
        using var client = isolated.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/admin/flows/approve_seller",
            new { p_seller = fixture.Seller, p_package = Guid.Parse("20000000-0000-0000-0000-00000000000a") })).StatusCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var received = Data.CreateCommand($"SELECT count(*) FROM {schema}.received WHERE \"Name\"=@name AND \"StatusName\"='Succeeded'");
        received.Parameters.AddWithValue("name", SellerEmailEvents.Requested);
        while ((long)(await received.ExecuteScalarAsync(deadline.Token))! == 0) await Task.Delay(100, deadline.Token);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(fixture.User + "@example.test", handler.Recipient);
    }

    private sealed class Provider : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public string? Recipient { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Recipient = json.RootElement.GetProperty("to")[0].GetProperty("email").GetString();
            return new(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created) { Content = new StringContent("{\"messageId\":\"fake\"}") };
        }
    }
}
