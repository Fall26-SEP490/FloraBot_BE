using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class StockTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task StockingProtectsTenantRecordsSnapshotAndRejectsDuplicate()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var foreignSeller = Guid.NewGuid(); var user = Guid.NewGuid();
        var product = Guid.NewGuid(); var foreignProduct = Guid.NewGuid(); var kiosk = Guid.NewGuid(); var foreignKiosk = Guid.NewGuid();
        var slot = Guid.NewGuid(); var emptySlot = Guid.NewGuid(); var foreignSlot = Guid.NewGuid();
        var annualPackage = Guid.Parse("20000000-0000-0000-0000-00000000000d");
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            SELECT id,'Stock test','0901234567','ACTIVE',@package,CURRENT_DATE+365 FROM unnest(ARRAY[@seller,@foreignSeller]::uuid[]) AS id;
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Stock test','SELLER',@seller);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Stock flower',350000,48,'ACTIVE'),(@foreignProduct,@foreignSeller,'Foreign flower',999000,24,'ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Stock kiosk','Stock address','HCM',@kiosk::text,@kiosk::text,'ONLINE'),
                  (@foreignKiosk,@foreignKiosk::text,'Foreign stock kiosk','Foreign stock address','HCM',@foreignKiosk::text,@foreignKiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone)
            VALUES(@slot,@kiosk,'A01',0,'WALK_IN'),(@emptySlot,@kiosk,'A02',1,'WALK_IN'),(@foreignSlot,@foreignKiosk,'A01',0,'WALK_IN');
            """);
        foreach (var (name, id) in new[] { ("seller", seller), ("foreignSeller", foreignSeller), ("user", user), ("product", product), ("foreignProduct", foreignProduct), ("kiosk", kiosk), ("foreignKiosk", foreignKiosk), ("slot", slot), ("emptySlot", emptySlot), ("foreignSlot", foreignSlot) }) setup.Parameters.AddWithValue(name, id);
        setup.Parameters.AddWithValue("package", annualPackage);
        await setup.ExecuteNonQueryAsync();
        await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(data, kiosk, seller, annualPackage);
        await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(data, foreignKiosk, foreignSeller, annualPackage);
        // Fixtures stay in the isolated test database because inventory and audit are append-only.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));
        const string route = "/api/me/flows/stock_bouquet";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/me/kiosks/{foreignKiosk}/slots")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { productId = foreignProduct, slotId = slot })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { productId = product, slotId = foreignSlot })).StatusCode);
        var before = DateTime.UtcNow;
        var command = new { productId = product, slotId = slot };
        var results = await Task.WhenAll(client.PostAsJsonAsync(route, command), client.PostAsJsonAsync(route, command));
        var successful = Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        var result = await successful.Content.ReadFromJsonAsync<JsonElement>();
        var batch = result.GetProperty("batchId").GetGuid();
        Assert.NotEqual(Guid.Empty, batch);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        await using var changePrice = data.CreateCommand("UPDATE catalog.flower_products SET price=400000 WHERE id=@id");
        changePrice.Parameters.AddWithValue("id", product); await changePrice.ExecuteNonQueryAsync();
        var read = await client.GetAsync($"/api/me/kiosks/{kiosk}/slots");
        Assert.True(read.Headers.CacheControl?.NoStore);
        var rows = await read.Content.ReadFromJsonAsync<List<SellerSlotResponse>>();
        Assert.Equal(2, rows!.Count);
        Assert.DoesNotContain(rows, row => row.Id == foreignSlot);
        var stocked = Assert.Single(rows, row => row.Id == slot);
        Assert.Equal("STOCKED", stocked.Status); Assert.StartsWith("FB-", stocked.QrCode);
        Assert.Equal(350000L, stocked.PriceSnapshot); Assert.Equal(product, stocked.ProductId);
        Assert.Equal("Stock kiosk", stocked.KioskName);
        Assert.InRange(stocked.SellableUntil!.Value, before.AddHours(48).AddSeconds(-1), DateTime.UtcNow.AddHours(48).AddSeconds(1));
        await using var logs = data.CreateCommand("SELECT count(*) FROM kiosk_ops.inventory_logs WHERE batch_id=@batch AND performed_by=@user AND movement_type='STOCK_IN'");
        logs.Parameters.AddWithValue("batch", batch); logs.Parameters.AddWithValue("user", user);
        Assert.Equal(1L, await logs.ExecuteScalarAsync());
        await using var expire = data.CreateCommand("UPDATE identity.subscriptions SET status='EXPIRED' WHERE kiosk_id=@id AND status='ACTIVE'");
        expire.Parameters.AddWithValue("id", kiosk); await expire.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { productId = product, slotId = emptySlot })).StatusCode);
    }
}
