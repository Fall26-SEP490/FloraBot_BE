using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class FlowIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task ServerTimeAndActorCannotBeOverridden()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var response = await client.PostAsJsonAsync("/api/sellers/20000000-0000-0000-0000-000000000001/flows/subscribe",
            new { p_months = 1, p_now = "2099-01-01", p_user = "10000000-0000-0000-0000-000000000001" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task FlowBusinessErrorIsMappedToConflict()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var response = await client.PostAsJsonAsync("/api/sellers/20000000-0000-0000-0000-000000000001/flows/subscribe", new { p_months = 0 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Số tháng đăng ký phải từ 1 đến 12", await response.Content.ReadAsStringAsync());
    }
    [Fact]
    public async Task AdminCanChangeCatalogWithAuditAttribution()
    {
        const string product = "30000000-0000-0000-0000-000000000002";
        const string route = "/api/sellers/20000000-0000-0000-0000-000000000001/flows/";
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var price = await client.PostAsJsonAsync(route + "update_product_price", new { p_product = product, p_price = 417000 });
        Assert.Equal(HttpStatusCode.OK, price.StatusCode);
        var status = await client.PostAsJsonAsync(route + "set_product_status", new { p_product = product, p_status = "ACTIVE" });
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var check = data.CreateCommand("SELECT price=417000 AND status='ACTIVE' FROM catalog.flower_products WHERE id=@id");
        check.Parameters.AddWithValue("id", Guid.Parse(product));
        Assert.Equal(true, await check.ExecuteScalarAsync());
        await using var audit = data.CreateCommand("SELECT count(DISTINCT action) FROM notify.audit_logs WHERE actor_id='10000000-0000-0000-0000-000000000001' AND entity_id=@id AND action IN ('PRODUCT_PRICE_CHANGED','PRODUCT_STATUS_CHANGED')");
        audit.Parameters.AddWithValue("id", Guid.Parse(product));
        Assert.Equal(2L, await audit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task SellerCannotChangeForeignProductStatus()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var response = await client.PostAsJsonAsync("/api/sellers/20000000-0000-0000-0000-000000000001/flows/set_product_status",
            new { p_product = "30000000-0000-0000-0000-000000000003", p_status = "ACTIVE" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RegistrationStoresContactWithoutInventedEmailOrPassword()
    {
        using var client = factory.CreateClient();
        var phone = "09" + Random.Shared.Next(10000000, 99999999);
        var response = await client.PostAsJsonAsync("/api/sellers", new { shopName = "Tiệm kiểm thử", phone, area = "Thủ Đức", packageId = "20000000-0000-0000-0000-00000000000a" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var check = data.CreateCommand("SELECT email IS NULL AND password_hash='!unprovisioned' FROM identity.users WHERE seller_id=@id");
        check.Parameters.AddWithValue("id", body.GetProperty("id").GetGuid());
        Assert.Equal(true, await check.ExecuteScalarAsync());
    }
    [Fact]
    public async Task OpenApiIsProducedFromActualEndpoints()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/sellers/{sellerId}/flows/subscribe", out _));
        var schema = document.GetProperty("components").GetProperty("schemas").GetProperty("SubscribeRequest");
        Assert.False(schema.GetProperty("properties").TryGetProperty("p_now", out _));
        var exportPath = Environment.GetEnvironmentVariable("EXPORT_OPENAPI_PATH");
        if (!string.IsNullOrEmpty(exportPath)) await File.WriteAllTextAsync(exportPath, document.GetRawText());
    }
}
