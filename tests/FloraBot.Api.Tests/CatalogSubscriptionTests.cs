using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class CatalogSubscriptionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("ACTIVE", 1, true)]
    [InlineData("ACTIVE", 0, false)]
    [InlineData("PAST_DUE", -1, false)]
    [InlineData("APPROVED", 1, false)]
    [InlineData("SUSPENDED", 1, false)]
    [InlineData("CLOSED", 1, false)]
    public async Task SellerCatalogWritesRequireActiveUnexpiredPackage(string status, int days, bool allowed)
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var user = Guid.NewGuid(); var product = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Catalog test','0901112233',@status,'20000000-0000-0000-0000-00000000000a',CURRENT_DATE+@days);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status) VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Catalog test','SELLER',@seller,'ACTIVE');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours) VALUES(@product,@seller,'Test flower',350000,48);
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("user", user);
        setup.Parameters.AddWithValue("product", product); setup.Parameters.AddWithValue("status", status); setup.Parameters.AddWithValue("days", days);
        await setup.ExecuteNonQueryAsync();
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));
            var route = $"/api/sellers/{seller}/flows/";
            var price = await client.PostAsJsonAsync(route + "update_product_price", new { p_product = product, p_price = 351000 });
            var availability = await client.PostAsJsonAsync(route + "set_product_status", new { p_product = product, p_status = "ACTIVE" });
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Conflict, price.StatusCode);
            Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Conflict, availability.StatusCode);
            await using var check = data.CreateCommand("SELECT price FROM catalog.flower_products WHERE id=@id");
            check.Parameters.AddWithValue("id", product);
            Assert.Equal(allowed ? 351000L : 350000L, await check.ExecuteScalarAsync());
            await using var audit = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE actor_id=@actor AND entity_id=@product AND action='PRODUCT_PRICE_CHANGED'");
            audit.Parameters.AddWithValue("actor", user); audit.Parameters.AddWithValue("product", product);
            Assert.Equal(allowed ? 1L : 0L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            // Audit rows remain append-only, including in the isolated test database.
            await using var cleanup = data.CreateCommand("DELETE FROM catalog.flower_products WHERE id=@product; DELETE FROM identity.users WHERE id=@user; DELETE FROM identity.sellers WHERE id=@seller;");
            cleanup.Parameters.AddWithValue("product", product); cleanup.Parameters.AddWithValue("user", user); cleanup.Parameters.AddWithValue("seller", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
