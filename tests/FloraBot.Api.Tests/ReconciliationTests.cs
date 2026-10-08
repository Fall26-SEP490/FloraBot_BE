using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ReconciliationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"p_date\":\"2002-01-02\"}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":null}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":{}}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{}]}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{\"txn\":\"x\",\"amount\":0}]}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{\"txn\":\"x\",\"amount\":-1}]}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{\"txn\":\"x\",\"amount\":1.5}]}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{\"txn\":\"x\",\"amount\":\"100\"}]}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{\"txn\":\" x\",\"amount\":100}]}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{\"txn\":\"x\\n\",\"amount\":100}]}")]
    [InlineData("{\"p_date\":\"2002-01-02\",\"p_statement\":[{\"txn\":\"x\",\"amount\":9223372036854775807},{\"txn\":\"x\",\"amount\":1}]}")]
    public async Task InvalidStatementsReturnNonCacheableBadRequest(string json)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/admin/flows/reconcile_gateway", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task StatementRowsAreBoundedAndDuplicateAmountsAreSummed()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var row = new { txn = Guid.NewGuid().ToString(), amount = 100, account = "must-not-be-retained" };
        const string path = "/api/admin/flows/reconcile_gateway";
        var excessive = await client.PostAsJsonAsync(path, new { p_date = "2002-02-03", p_statement = Enumerable.Repeat(row, 5001) });
        Assert.Equal(HttpStatusCode.BadRequest, excessive.StatusCode);
        var response = await client.PostAsJsonAsync(path, new { p_date = "2002-02-03", p_statement = new[] { row, row } });
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").EnumerateArray().ToArray();
        var missing = Assert.Single(rows, x => x.GetProperty("gateway_txn_id").GetString() == row.txn);
        Assert.Equal(200, missing.GetProperty("sao_ke").GetInt64());
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var payment = data.CreateCommand("SELECT count(*) FROM payment.payments WHERE gateway_txn_id=@txn");
        payment.Parameters.AddWithValue("txn", row.txn);
        Assert.Equal(0L, await payment.ExecuteScalarAsync());
        var empty = await client.PostAsJsonAsync(path, new { p_date = "2002-02-03", p_statement = Array.Empty<object>() });
        empty.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task DailyReportRequiresAdminAndDateAndAuditsReturnedReport()
    {
        const string path = "/api/admin/flows/reconcile_daily";
        using var client = factory.CreateClient();
        var body = new { p_date = "2002-02-04" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, body)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, body)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var result = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
        Assert.Equal(6, result.GetArrayLength());
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var audit = data.CreateCommand("SELECT payload->'report' FROM notify.audit_logs WHERE action='RECONCILED_DAILY' AND payload->>'date'='2002-02-04' ORDER BY created_at DESC,id DESC LIMIT 1");
        using var report = JsonDocument.Parse((string)(await audit.ExecuteScalarAsync())!);
        Assert.True(JsonElement.DeepEquals(result, report.RootElement));
    }

    [Fact]
    public async Task SqlAlsoAggregatesDuplicateTransactionIds()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await data.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT sao_ke FROM flow.reconcile_gateway('2002-02-05',
              '[{"txn":"duplicate-fixture","amount":40},{"txn":"duplicate-fixture","amount":60}]'::jsonb,
              '10000000-0000-0000-0000-000000000001') WHERE gateway_txn_id='duplicate-fixture'
            """, connection, transaction);
        Assert.Equal(100L, await command.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task GatewayAuditMatchesTheReportedDateGatewayAndDifferences()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var prefix = Guid.NewGuid().ToString();
        const string date = "2002-01-02";
        await using var fixture = data.CreateCommand("""
            INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,gateway_txn_id,idempotency_key,amount,status,paid_at)
            SELECT 'CHARGE','ORDER_CHECKOUT',public.uuid_v7(),gateway,@prefix || suffix,@prefix || suffix,amount,'SUCCEEDED',paid
            FROM (VALUES
              ('PAYOS','-match',100,'2002-01-02 01:00:00+07'::timestamptz),
              ('PAYOS','-amount',200,'2002-01-02 02:00:00+07'::timestamptz),
              ('PAYOS','-missing',300,'2002-01-02 03:00:00+07'::timestamptz),
              ('PAYOS','-other-day',400,'2002-01-01 23:59:59+07'::timestamptz),
              ('MANUAL','-other-gateway',500,'2002-01-02 03:00:00+07'::timestamptz)
            ) AS v(gateway,suffix,amount,paid);
            """);
        fixture.Parameters.AddWithValue("prefix", prefix); await fixture.ExecuteNonQueryAsync();
        try
        {
            using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
            using var seller = factory.CreateClient(); seller.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
            var body = new
            {
                p_date = date,
                p_statement = new[] {
                new { txn = prefix + "-match", amount = 100 }, new { txn = prefix + "-amount", amount = 250 },
                new { txn = prefix + "-other-day", amount = 400 }, new { txn = prefix + "-other-gateway", amount = 500 }
            }
            };
            const string path = "/api/admin/flows/reconcile_gateway";
            Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsJsonAsync(path, body)).StatusCode);
            var response = await admin.PostAsJsonAsync(path, body); response.EnsureSuccessStatusCode();
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").EnumerateArray().ToArray();
            Assert.Equal(4, rows.Length);
            Assert.Equal(2, rows.Count(x => x.GetProperty("loai").GetString() == "MISSING_IN_DB"));
            Assert.Single(rows, x => x.GetProperty("loai").GetString() == "AMOUNT_MISMATCH");
            Assert.Single(rows, x => x.GetProperty("loai").GetString() == "MISSING_IN_STATEMENT");
            await using var audit = data.CreateCommand("SELECT payload FROM notify.audit_logs WHERE action='RECONCILED' AND payload->>'date'=@date ORDER BY created_at DESC,id DESC LIMIT 1");
            audit.Parameters.AddWithValue("date", date);
            using var payload = JsonDocument.Parse((string)(await audit.ExecuteScalarAsync())!);
            Assert.Equal(1, payload.RootElement.GetProperty("matched").GetInt32());
            Assert.Equal(rows.Length, payload.RootElement.GetProperty("diff").GetInt32());
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM payment.payments WHERE idempotency_key LIKE @prefix");
            cleanup.Parameters.AddWithValue("prefix", prefix + "%"); await cleanup.ExecuteNonQueryAsync();
        }
    }
}
