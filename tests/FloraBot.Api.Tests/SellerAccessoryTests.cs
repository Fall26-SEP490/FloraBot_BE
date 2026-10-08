using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SellerAccessoryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task MerchantInventoryAndRestockProtectTenantAndRecordActor()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var otherSeller = Guid.NewGuid(); var user = Guid.NewGuid();
        var kiosk = Guid.NewGuid(); var item = Guid.NewGuid(); var otherItem = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            SELECT id,'Accessory shop fixture','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30 FROM unnest(ARRAY[@seller,@otherSeller]::uuid[]) id;
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Accessory seller','SELLER',@seller);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Accessory kiosk','Fixture address','HCM',@kiosk::text,@kiosk::text,'OFFLINE');
            INSERT INTO kiosk_ops.accessories(id,seller_id,kiosk_id,name,price,stock_quantity,status)
            VALUES(@item,@seller,@kiosk,'Ribbon fixture',25000,0,'INACTIVE'),(@otherItem,@otherSeller,@kiosk,'Other accessory',30000,11,'ACTIVE');
            """);
        foreach (var pair in new[] { ("seller", seller), ("otherSeller", otherSeller), ("user", user), ("kiosk", kiosk), ("item", item), ("otherItem", otherItem) })
            setup.Parameters.AddWithValue(pair.Item1, pair.Item2);
        await setup.ExecuteNonQueryAsync();
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));
        var path = $"/api/sellers/{seller}/accessories";
        var write = $"/api/sellers/{seller}/flows/restock_accessory";
        var response = await client.GetAsync(path); response.EnsureSuccessStatusCode(); Assert.True(response.Headers.CacheControl?.NoStore);
        var row = Assert.Single((await response.Content.ReadFromJsonAsync<List<SellerAccessoryResponse>>())!);
        Assert.Equal(item, row.Id); Assert.Equal(0, row.StockQuantity); Assert.Equal("INACTIVE", row.Status);
        Assert.Equal(kiosk, row.KioskId); Assert.Equal("OFFLINE", row.KioskStatus); Assert.Equal("Fixture address", row.KioskAddress);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sellers/{otherSeller}/accessories")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(write, new { p_accessory = otherItem, p_qty = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/sellers/{otherSeller}/flows/restock_accessory", new { p_accessory = otherItem, p_qty = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(write, new { p_accessory = item, p_qty = 2, p_user = Guid.NewGuid() })).StatusCode);
        foreach (var quantity in new[] { 0, -1 })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(write, new { p_accessory = item, p_qty = quantity })).StatusCode);
        (await client.PostAsJsonAsync(write, new { p_accessory = item, p_qty = 7 })).EnsureSuccessStatusCode();
        var updated = Assert.Single((await client.GetFromJsonAsync<List<SellerAccessoryResponse>>(path))!);
        Assert.Equal(7, updated.StockQuantity); Assert.Equal("INACTIVE", updated.Status); Assert.Equal(25000, updated.Price);
        await using var verify = data.CreateCommand("""
            SELECT (SELECT stock_quantity=11 FROM kiosk_ops.accessories WHERE id=@other)
              AND (SELECT count(*)=1 FROM notify.audit_logs WHERE action='ACCESSORY_RESTOCKED' AND actor_id=@user AND entity_id=@item AND payload->>'qty'='7');
            """);
        verify.Parameters.AddWithValue("other", otherItem); verify.Parameters.AddWithValue("user", user); verify.Parameters.AddWithValue("item", item);
        Assert.Equal(true, await verify.ExecuteScalarAsync());
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(otherItem, Assert.Single((await admin.GetFromJsonAsync<List<SellerAccessoryResponse>>($"/api/sellers/{otherSeller}/accessories"))!).Id);
    }
}
