using DotNetCore.CAP;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class OutboxTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task RollbackIsAtomicAndCommittedMessageRecoversAfterBrokerOutageAndRestart()
    {
        var schema = "cap_test_" + Guid.NewGuid().ToString("N");
        var payment = Guid.NewGuid();
        void Configure(IWebHostBuilder builder, bool offline)
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<PostgreSqlOptions>(options => options.Schema = schema);
                services.PostConfigure<RabbitMQOptions>(options =>
                {
                    options.HostName = "127.0.0.1";
                    options.Port = offline ? 1 : 55672;
                    options.ExchangeName = schema;
                    options.ConnectionFactoryOptions = connection => connection.RequestedConnectionTimeout = TimeSpan.FromSeconds(1);
                });
                services.PostConfigure<CapOptions>(options =>
                {
                    options.DefaultGroupName = schema;
                    options.FailedRetryInterval = 1;
                    options.FallbackWindowLookbackSeconds = 1;
                });
            });
        }
        using (var offline = factory.WithWebHostBuilder(builder => Configure(builder, true)))
        {
            using var client = offline.CreateClient();
            using var scope = offline.Services.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            var publisher = scope.ServiceProvider.GetRequiredService<ICapPublisher>();
            await using var connection = await data.OpenConnectionAsync();
            async Task Write(bool commit)
            {
                using var outbox = await connection.BeginTransactionAsync(publisher);
                var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
                await using var business = new NpgsqlCommand("SELECT flow.audit(NULL,'OUTBOX_ATOMICITY_TEST','payments',@id)", connection, transaction);
                business.Parameters.AddWithValue("id", payment);
                await business.ExecuteNonQueryAsync();
                await publisher.PublishAsync(PaymentEvents.Succeeded, new PaymentSucceeded(payment, Guid.NewGuid(), null, 100, "outbox-test", DateTime.UtcNow));
                if (commit) await outbox.CommitAsync();
                else await outbox.RollbackAsync();
            }
            await Write(false);
            await using var check = new NpgsqlCommand($"SELECT (SELECT count(*) FROM {schema}.published), (SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id)", connection);
            check.Parameters.AddWithValue("id", payment);
            await using (var reader = await check.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(0, reader.GetInt64(0)); Assert.Equal(0, reader.GetInt64(1));
            }
            await Write(true);
            await using (var reader = await check.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1, reader.GetInt64(0)); Assert.Equal(1, reader.GetInt64(1));
            }
            await using var state = new NpgsqlCommand($"SELECT \"StatusName\" FROM {schema}.published", connection);
            Assert.NotEqual("Succeeded", await state.ExecuteScalarAsync());
        }
        using var recovered = factory.WithWebHostBuilder(builder => Configure(builder, false));
        using var recoveredClient = recovered.CreateClient();
        using var recoveredScope = recovered.Services.CreateScope();
        var recoveredData = recoveredScope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var delivered = recoveredData.CreateCommand($"SELECT count(*) FROM {schema}.received WHERE \"StatusName\"='Succeeded' AND \"Content\"::jsonb->'Value'->>'payment_id'=@id");
        delivered.Parameters.AddWithValue("id", payment.ToString());
        while ((long)(await delivered.ExecuteScalarAsync(deadline.Token))! == 0)
            await Task.Delay(250, deadline.Token);
    }
}
