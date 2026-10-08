using System.Net;
using System.Net.Http.Json;
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
        var product = Guid.NewGuid(); var foreignProduct = Guid.NewGuid(); var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid(); var emptySlot = Guid.NewGuid(); var foreignSlot = Guid.NewGuid(); var batch = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            SELECT id,'Stock test','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30 FROM unnest(ARRAY[@seller,@foreignSeller]::uuid[]) AS id;
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Stock test','SELLER',@seller);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status)
            VALUES(@product,@seller,'Stock flower',350000,48,'ACTIVE'),(@foreignProduct,@foreignSeller,'Foreign flower',999000,24,'ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Stock kiosk','Stock address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel)
            VALUES(@slot,@kiosk,'A01',0),(@emptySlot,@kiosk,'A02',1),(@foreignSlot,@kiosk,'A03',2);
            SELECT flow.assign_slot(@slot,@seller,'10000000-0000-0000-0000-000000000001');
            SELECT flow.assign_slot(@emptySlot,@seller,'10000000-0000-0000-0000-000000000001');
            SELECT flow.assign_slot(@foreignSlot,@foreignSeller,'10000000-0000-0000-0000-000000000001');
            """);
        foreach (var (name, id) in new[] { ("seller", seller), ("foreignSeller", foreignSeller), ("user", user), ("product", product), ("foreignProduct", foreignProduct), ("kiosk", kiosk), ("slot", slot), ("emptySlot", emptySlot), ("foreignSlot", foreignSlot) }) setup.Parameters.AddWithValue(name, id);
        await setup.ExecuteNonQueryAsync();
        // Fixtures stay in the isolated test database because inventory and audit are append-only.
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));
        var route = $"/api/sellers/{seller}/flows/stock_bouquet";
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sellers/{foreignSeller}/slots")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { p_batch = batch, p_product = foreignProduct, p_slot = slot, p_qr = "foreign-product" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(route, new { p_batch = batch, p_product = product, p_slot = foreignSlot, p_qr = "foreign-slot" })).StatusCode);
        var before = DateTime.UtcNow;
        var command = new { p_batch = batch, p_product = product, p_slot = slot, p_qr = "stock-" + slot };
        var results = await Task.WhenAll(client.PostAsJsonAsync(route, command), client.PostAsJsonAsync(route, command));
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        await using var changePrice = data.CreateCommand("UPDATE catalog.flower_products SET price=400000 WHERE id=@id");
        changePrice.Parameters.AddWithValue("id", product); await changePrice.ExecuteNonQueryAsync();
        var read = await client.GetAsync($"/api/sellers/{seller}/slots");
        Assert.True(read.Headers.CacheControl?.NoStore);
        var rows = await read.Content.ReadFromJsonAsync<List<SellerSlotResponse>>();
        Assert.Equal(2, rows!.Count);
        Assert.DoesNotContain(rows, row => row.Id == foreignSlot);
        var stocked = Assert.Single(rows, row => row.Id == slot);
        Assert.Equal("STOCKED", stocked.Status); Assert.Equal(command.p_qr, stocked.QrCode);
        Assert.Equal(350000L, stocked.PriceSnapshot); Assert.Equal(product, stocked.ProductId);
        Assert.Equal("Stock kiosk", stocked.KioskName);
        Assert.InRange(stocked.SellableUntil!.Value, before.AddHours(48).AddSeconds(-1), DateTime.UtcNow.AddHours(48).AddSeconds(1));
        await using var logs = data.CreateCommand("SELECT count(*) FROM kiosk_ops.inventory_logs WHERE batch_id=@batch AND performed_by=@user AND movement_type='STOCK_IN'");
        logs.Parameters.AddWithValue("batch", batch); logs.Parameters.AddWithValue("user", user);
        Assert.Equal(1L, await logs.ExecuteScalarAsync());
        await using var expire = data.CreateCommand("UPDATE identity.sellers SET package_expires_at=CURRENT_DATE-1 WHERE id=@id");
        expire.Parameters.AddWithValue("id", seller); await expire.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, new { p_batch = batch, p_product = product, p_slot = emptySlot, p_qr = "expired-" + slot })).StatusCode);
    }
}
