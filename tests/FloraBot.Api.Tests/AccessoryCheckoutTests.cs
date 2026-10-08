using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AccessoryCheckoutTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task CatalogAndCheckoutRespectKioskSellerStockAndAtomicRestoration()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var kiosk = Guid.NewGuid(); var otherKiosk = Guid.NewGuid(); var customer = Guid.NewGuid();
        var seller = Guid.NewGuid(); var secondSeller = Guid.NewGuid(); var pausedSeller = Guid.NewGuid();
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var key = "accessory-fixture-" + kiosk;
        await using var setup = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at) VALUES
              (@seller,'Same shop name','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30),
              (@secondSeller,'Same shop name','0901112234','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30),
              (@pausedSeller,'Paused shop','0901112235','PAST_DUE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE-1);
            INSERT INTO identity.users(id,email,full_name,role) VALUES(@customer,@customer::text||'@example.invalid','Accessory customer','CUSTOMER');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash,last_heartbeat_at)
            VALUES(@kiosk,@kiosk::text,'Accessory fixture','Test','Test',@kiosk::text,@kiosk::text,'ONLINE',@hash,CURRENT_TIMESTAMP),
              (@other,@other::text,'Other fixture','Test','Test',@other::text,@other::text,'ONLINE','unused-fixture-key-hash',CURRENT_TIMESTAMP);
            INSERT INTO kiosk_ops.accessories(id,seller_id,kiosk_id,name,price,stock_quantity) VALUES
              (@first,@seller,@kiosk,'Ribbon',20000,5),(@second,@secondSeller,@kiosk,'Card',5000,7);
            INSERT INTO kiosk_ops.accessories(seller_id,kiosk_id,name,price,stock_quantity,status) VALUES
              (@seller,@kiosk,'Inactive',1000,10,'INACTIVE'),(@seller,@kiosk,'Empty',1000,0,'ACTIVE'),
              (@pausedSeller,@kiosk,'Paused seller',1000,10,'ACTIVE'),(@seller,@other,'Other kiosk',1000,10,'ACTIVE');
            """);
        foreach (var pair in new[] { ("kiosk", kiosk), ("other", otherKiosk), ("customer", customer), ("seller", seller), ("secondSeller", secondSeller), ("pausedSeller", pausedSeller), ("first", first), ("second", second) })
            setup.Parameters.AddWithValue(pair.Item1, pair.Item2);
        setup.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        await setup.ExecuteNonQueryAsync();
        var catalogPath = $"/api/kiosks/{kiosk}/catalog/accessories";
        var checkoutPath = $"/api/kiosks/{kiosk}/flows/kiosk_checkout";
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(catalogPath)).StatusCode);
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(catalogPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/kiosks/{otherKiosk}/catalog/accessories")).StatusCode);
        var response = await client.GetAsync(catalogPath); response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.CacheControl?.NoStore);
        var catalog = (await response.Content.ReadFromJsonAsync<List<KioskAccessoryResponse>>())!;
        Assert.Equal(2, catalog.Count); Assert.Equal(new[] { "Card", "Ribbon" }, catalog.Select(x => x.Name));
        Assert.Equal(5, catalog.Single(x => x.Id == first).StockQuantity);
        Assert.Equal(20000, catalog.Single(x => x.Id == first).Price);
        using var signed = factory.CreateClient();
        signed.DefaultRequestHeaders.Authorization = new("Bearer", scope.ServiceProvider.GetRequiredService<TokenService>().Issue(new User { Id = customer, Role = "CUSTOMER", FullName = "Fixture" }, kioskId: kiosk.ToString()));
        Assert.Equal(2, (await signed.GetFromJsonAsync<List<KioskAccessoryResponse>>(catalogPath))!.Count);

        foreach (var quantities in new[] { (0, 1), (2, 8) })
        {
            var rejected = await client.PostAsJsonAsync(checkoutPath, new { p_bouquets = Array.Empty<Guid>(), p_accessories = new[] { new { id = first, qty = quantities.Item1 }, new { id = second, qty = quantities.Item2 } } });
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        }
        await using var untouched = source.CreateCommand("SELECT (SELECT stock_quantity=5 FROM kiosk_ops.accessories WHERE id=@first) AND (SELECT stock_quantity=7 FROM kiosk_ops.accessories WHERE id=@second) AND NOT EXISTS(SELECT 1 FROM ordering.orders WHERE kiosk_id=@kiosk)");
        untouched.Parameters.AddWithValue("first", first); untouched.Parameters.AddWithValue("second", second); untouched.Parameters.AddWithValue("kiosk", kiosk);
        Assert.Equal(true, await untouched.ExecuteScalarAsync());
        var created = await client.PostAsJsonAsync(checkoutPath, new
        {
            p_bouquets = Array.Empty<Guid>(),
            p_accessories = new[] { new { id = first, qty = 2, price = 1, seller_id = pausedSeller }, new { id = second, qty = 3, price = 1, seller_id = pausedSeller } }
        });
        created.EnsureSuccessStatusCode();
        var checkout = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var payment = (await client.GetFromJsonAsync<CheckoutResponse>($"/api/kiosks/{kiosk}/checkouts/{checkout}"))!;
        Assert.Equal(55000, payment.Amount); Assert.Equal(2, payment.Orders.Count);
        await using var state = source.CreateCommand("""
            SELECT (SELECT stock_quantity=3 FROM kiosk_ops.accessories WHERE id=@first)
              AND (SELECT stock_quantity=4 FROM kiosk_ops.accessories WHERE id=@second)
              AND (SELECT count(*)=2 FROM ordering.orders WHERE checkout_id=@checkout AND seller_id=ANY(@sellers))
              AND (SELECT sum(line_total)=55000 FROM ordering.order_items WHERE order_id IN (SELECT id FROM ordering.orders WHERE checkout_id=@checkout));
            """);
        state.Parameters.AddWithValue("first", first); state.Parameters.AddWithValue("second", second);
        state.Parameters.AddWithValue("checkout", checkout); state.Parameters.AddWithValue("sellers", new[] { seller, secondSeller });
        Assert.Equal(true, await state.ExecuteScalarAsync());
        await using var release = source.CreateCommand("""
            UPDATE payment.payments SET status='EXPIRED' WHERE checkout_id=@checkout AND status='PENDING';
            SELECT flow.release_checkout(@checkout,'EXPIRED'); SELECT flow.release_checkout(@checkout,'EXPIRED');
            SELECT (SELECT stock_quantity=5 FROM kiosk_ops.accessories WHERE id=@first) AND (SELECT stock_quantity=7 FROM kiosk_ops.accessories WHERE id=@second);
            """);
        release.Parameters.AddWithValue("checkout", checkout); release.Parameters.AddWithValue("first", first); release.Parameters.AddWithValue("second", second);
        await release.ExecuteNonQueryAsync();
        var restored = (await client.GetFromJsonAsync<List<KioskAccessoryResponse>>(catalogPath))!;
        Assert.Equal(5, restored.Single(x => x.Id == first).StockQuantity); Assert.Equal(7, restored.Single(x => x.Id == second).StockQuantity);

        var race = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync(checkoutPath, new { p_bouquets = Array.Empty<Guid>(), p_accessories = new[] { new { id = first, qty = 5 } } })));
        Assert.Single(race, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(race, x => x.StatusCode == HttpStatusCode.Conflict);
        var winner = (await race.Single(x => x.IsSuccessStatusCode).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        await using var pay = source.CreateCommand("SELECT flow.checkout_paid(@checkout,@txn,true,100000)");
        pay.Parameters.AddWithValue("checkout", winner); pay.Parameters.AddWithValue("txn", "accessory-fixture-" + winner);
        await pay.ExecuteNonQueryAsync();
        var completed = (await client.GetFromJsonAsync<CheckoutResponse>($"/api/kiosks/{kiosk}/checkouts/{winner}"))!;
        Assert.Equal("COMPLETED", Assert.Single(completed.Orders).Status); Assert.Equal(100000, completed.Amount);
        await using var noDoor = source.CreateCommand("SELECT count(*) FROM kiosk_ops.unlock_tokens WHERE order_id=@order");
        noDoor.Parameters.AddWithValue("order", completed.Orders[0].Id); Assert.Equal(0L, await noDoor.ExecuteScalarAsync());
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<KioskAccessoryResponse>>(catalogPath))!, x => x.Id == first);
    }
}
