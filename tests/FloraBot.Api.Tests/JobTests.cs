using FloraBot.Api.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using StackExchange.Redis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class JobTests : IAsyncLifetime
{
    private static readonly string ConnectionString = Environment.GetEnvironmentVariable("TEST_JOBS_DATABASE_URL") ?? "Host=localhost;Port=55436;Database=florabot_jobs_tests;Username=florabot;Password=local-florabot-only";
    private readonly NpgsqlDataSource data = NpgsqlDataSource.Create(ConnectionString);
    private IConnectionMultiplexer redis = null!;
    private JobRunner runner = null!;
    private readonly ApiFactory baseFactory = new();
    private WebApplicationFactory<Program> host = null!;

    public async Task InitializeAsync()
    {
        redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("TEST_VALKEY_URL") ?? "localhost:56379");
        host = baseFactory.WithWebHostBuilder(builder => builder.UseSetting("DATABASE_URL", ConnectionString));
        Assert.Equal(new NpgsqlConnectionStringBuilder(data.ConnectionString).Database,
            new NpgsqlConnectionStringBuilder(host.Services.GetRequiredService<NpgsqlDataSource>().ConnectionString).Database);
        runner = new JobRunner(data, redis, NullLogger<JobRunner>.Instance, host.Services.GetRequiredService<IServiceScopeFactory>());
    }
    public async Task DisposeAsync() { await baseFactory.DisposeAsync(); await data.DisposeAsync(); await redis.CloseAsync(); redis.Dispose(); }

    [Fact]
    public async Task DailyAuditWriteRollsBackWithTheCallingTransaction()
    {
        var marker = Guid.NewGuid().ToString();
        await using var connection = await data.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var setup = new NpgsqlCommand("SET LOCAL TIME ZONE 'Asia/Ho_Chi_Minh'; SELECT (CURRENT_DATE-1)::text", connection, transaction);
        var date = (string)(await setup.ExecuteScalarAsync())!;
        var payload = System.Text.Json.JsonSerializer.Serialize(new { date, rows = Array.Empty<object>(), marker });
        await FloraBot.Api.Modules.Notify.ReconciliationAudit.RecordDailyJobAsync(connection, transaction, payload, CancellationToken.None);
        Assert.True(await FloraBot.Api.Modules.Notify.ReconciliationAudit.HasDailyJobAsync(connection, transaction, CancellationToken.None));
        await using var check = new NpgsqlCommand("SELECT count(*) FROM notify.audit_logs WHERE action='DAILY_RECONCILIATION' AND payload->>'marker'=@marker", connection, transaction);
        check.Parameters.AddWithValue("marker", marker);
        Assert.Equal(1L, await check.ExecuteScalarAsync());
        await transaction.RollbackAsync();
        await using var after = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='DAILY_RECONCILIATION' AND payload->>'marker'=@marker");
        after.Parameters.AddWithValue("marker", marker);
        Assert.Equal(0L, await after.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EveryScheduledFlowRunsAndDailyReportIsDurable()
    {
        foreach (var name in JobRunner.Names) await runner.RunAsync(name, CancellationToken.None);
        var repeated = await runner.RunAsync("reconcile_daily", CancellationToken.None);
        Assert.False(repeated.Executed);
        await using var query = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='DAILY_RECONCILIATION' AND payload->>'date'=((CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date-1)::text");
        Assert.Equal(1L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UnpaidSubscriptionExpiresUsingServerClock()
    {
        await using var create = data.CreateCommand("SELECT flow.subscribe('20000000-0000-0000-0000-000000000001','10000000-0000-0000-0000-000000000003',1)");
        var subscription = (Guid)(await create.ExecuteScalarAsync())!;
        await using var age = data.CreateCommand("UPDATE identity.subscriptions SET created_at=CURRENT_TIMESTAMP-interval '7 days' WHERE id=@id");
        age.Parameters.AddWithValue("id", subscription);
        await age.ExecuteNonQueryAsync();
        var result = await runner.RunAsync("expire_pending_subscriptions", CancellationToken.None);
        Assert.True(result.Executed);
        Assert.True(result.Affected >= 1);
        await using var check = data.CreateCommand("SELECT s.status='CANCELLED' AND p.status='EXPIRED' FROM identity.subscriptions s JOIN payment.payments p ON p.id=s.payment_id WHERE s.id=@id");
        check.Parameters.AddWithValue("id", subscription);
        Assert.Equal(true, await check.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExistingValkeyLeaseIsNotStolenOrDeleted()
    {
        var key = runner.LockKey("mark_offline_kiosks");
        var cache = redis.GetDatabase();
        await cache.StringSetAsync(key, "another-worker", TimeSpan.FromSeconds(30));
        try
        {
            Assert.False((await runner.RunAsync("mark_offline_kiosks", CancellationToken.None)).Executed);
            Assert.Equal("another-worker", (string?)await cache.StringGetAsync(key));
        }
        finally { await cache.KeyDeleteAsync(key); }
    }

    [Fact]
    public async Task DatabaseLockPreventsOverlapEvenWithoutValkeyLease()
    {
        await using var connection = await data.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var guard = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", connection, transaction);
        guard.Parameters.AddWithValue("key", JobRunner.AdvisoryKey("mark_offline_kiosks"));
        await guard.ExecuteNonQueryAsync();
        Assert.False((await runner.RunAsync("mark_offline_kiosks", CancellationToken.None)).Executed);
        await transaction.RollbackAsync();
        Assert.True((await runner.RunAsync("mark_offline_kiosks", CancellationToken.None)).Executed);
    }

    [Fact]
    public async Task ExpiredSellerPublishesPastDueOnceAcrossRepeatedJobs()
    {
        var seller = Guid.NewGuid();
        await using var create = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES (@id,'Job transition fixture','0900000000','ACTIVE',
              (SELECT id FROM identity.subscription_packages ORDER BY id LIMIT 1),
              (CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date-1)
            RETURNING package_expires_at
            """);
        create.Parameters.AddWithValue("id", seller);
        var expiry = (DateOnly)(await create.ExecuteScalarAsync())!;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True((await runner.RunAsync("roll_subscriptions", CancellationToken.None)).Executed);
            await using var status = data.CreateCommand("SELECT status FROM identity.sellers WHERE id=@id");
            status.Parameters.AddWithValue("id", seller);
            Assert.Equal("PAST_DUE", await status.ExecuteScalarAsync());
            await using var events = data.CreateCommand("""
                SELECT "Content"::jsonb->'Value'->>'package_expires_at' FROM cap.published
                WHERE "Name"='florabot.seller.past-due.v1'
                  AND "Content"::jsonb->'Value'->>'seller_id'=@id
                """);
            events.Parameters.AddWithValue("id", seller.ToString());
            await using var reader = await events.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(expiry.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), reader.GetString(0));
            Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task ExpiredPickupPublishesFailureAndRefundOnce()
    {
        var seller = Guid.NewGuid(); var product = Guid.NewGuid();
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Expiry fixture','0900000000','ACTIVE',
              '20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Expiry bouquet',350000,48,'ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Expiry kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,'A01',0);
            SELECT flow.assign_slot(@slot,@seller,'10000000-0000-0000-0000-000000000001');
            """);
        foreach (var (name, id) in new[] { ("seller", seller), ("product", product), ("kiosk", kiosk), ("slot", slot) })
            setup.Parameters.AddWithValue(name, id);
        await setup.ExecuteNonQueryAsync();
        await using var checkoutCommand = data.CreateCommand("""
            SELECT flow.kiosk_checkout(@kiosk,ARRAY[flow.stock_bouquet(flow.new_batch(),@product,@slot,
              '10000000-0000-0000-0000-000000000001',@qr)])
            """);
        checkoutCommand.Parameters.AddWithValue("kiosk", kiosk);
        checkoutCommand.Parameters.AddWithValue("product", product);
        checkoutCommand.Parameters.AddWithValue("slot", slot);
        checkoutCommand.Parameters.AddWithValue("qr", product.ToString());
        var checkout = (Guid)(await checkoutCommand.ExecuteScalarAsync())!;
        await using var paid = data.CreateCommand("SELECT flow.checkout_paid(@id,@txn,true,350000)");
        paid.Parameters.AddWithValue("id", checkout); paid.Parameters.AddWithValue("txn", checkout.ToString());
        await paid.ExecuteNonQueryAsync();
        await using var age = data.CreateCommand("UPDATE kiosk_ops.slots SET hold_until=CURRENT_TIMESTAMP-interval '1 minute' WHERE id=@id RETURNING hold_order_id");
        age.Parameters.AddWithValue("id", slot);
        var order = (Guid)(await age.ExecuteScalarAsync())!;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True((await runner.RunAsync("expire_pickups", CancellationToken.None)).Executed);
            await using var events = data.CreateCommand("""
                SELECT "Name","Content"::jsonb->'Value' FROM cap.published
                WHERE "Content"::jsonb->'Value'->>'order_id'=@id ORDER BY "Name"
                """);
            events.Parameters.AddWithValue("id", order.ToString());
            await using var reader = await events.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("florabot.dispense.failed.v1", reader.GetString(0));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("florabot.refund.approved.v1", reader.GetString(0));
            using var refund = System.Text.Json.JsonDocument.Parse(reader.GetString(1));
            Assert.Equal(350000, refund.RootElement.GetProperty("amount").GetInt64());
            Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task UnknownFunctionCannotBeRun()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync("checkout_paid", CancellationToken.None));
    }
}
