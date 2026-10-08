using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SlotAssignmentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AssignmentRequiresAdminPaidPackageAndSerializesPackageLimit()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var kiosk = Guid.NewGuid();
        var slots = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id)
            VALUES(@seller,'Slot assignment test','0901234567','APPROVED','20000000-0000-0000-0000-00000000000a');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Assignment kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel)
            SELECT id,@kiosk,'A' || ordinal::text,(ordinal-1)::smallint FROM unnest(@slots::uuid[]) WITH ORDINALITY AS s(id,ordinal);
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("kiosk", kiosk); setup.Parameters.AddWithValue("slots", slots);
        await setup.ExecuteNonQueryAsync();
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/slots")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/flows/assign_slot", new { p_slot = slots[0], p_seller = seller })).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/flows/assign_slot", new { p_slot = slots[0], p_seller = seller })).StatusCode);
            await using var expired = data.CreateCommand("UPDATE identity.sellers SET status='ACTIVE',package_expires_at=CURRENT_DATE-1 WHERE id=@id");
            expired.Parameters.AddWithValue("id", seller); await expired.ExecuteNonQueryAsync();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/flows/assign_slot", new { p_slot = slots[0], p_seller = seller })).StatusCode);
            await using var activate = data.CreateCommand("UPDATE identity.sellers SET status='ACTIVE',package_expires_at=CURRENT_DATE+30 WHERE id=@id");
            activate.Parameters.AddWithValue("id", seller); await activate.ExecuteNonQueryAsync();
            await using var fault = data.CreateCommand("UPDATE kiosk_ops.slots SET status='FAULT' WHERE id=@id");
            fault.Parameters.AddWithValue("id", slots[0]); await fault.ExecuteNonQueryAsync();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/flows/assign_slot", new { p_slot = slots[0], p_seller = seller })).StatusCode);
            await using var restore = data.CreateCommand("UPDATE kiosk_ops.slots SET status='FREE' WHERE id=@id");
            restore.Parameters.AddWithValue("id", slots[0]); await restore.ExecuteNonQueryAsync();
            var responses = await Task.WhenAll(slots.Select(slot => client.PostAsJsonAsync("/api/admin/flows/assign_slot", new { p_slot = slot, p_seller = seller })));
            Assert.Equal(3, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            var list = await client.GetAsync("/api/admin/slots");
            Assert.True(list.Headers.CacheControl?.NoStore);
            var rows = (await list.Content.ReadFromJsonAsync<List<AdminSlotResponse>>())!.Where(row => row.KioskId == kiosk).ToArray();
            Assert.Equal(4, rows.Length);
            Assert.Equal(3, rows.Count(row => row.Status == "RENTED_EMPTY" && row.CurrentSellerId == seller));
            Assert.All(rows, row => { Assert.Equal("Assignment kiosk", row.KioskName); Assert.Equal("Test address", row.KioskAddress); });
            var rented = rows.First(row => row.Status == "RENTED_EMPTY");
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/flows/assign_slot", new { p_slot = rented.Id, p_seller = seller })).StatusCode);
            await using var audit = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE entity_id=ANY(@slots) AND action='SLOT_ASSIGNED' AND actor_id='10000000-0000-0000-0000-000000000001'");
            audit.Parameters.AddWithValue("slots", slots);
            Assert.Equal(3L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = data.CreateCommand("DELETE FROM kiosk_ops.slot_assignments WHERE seller_id=@seller; DELETE FROM kiosk_ops.slots WHERE kiosk_id=@kiosk; DELETE FROM kiosk_ops.kiosks WHERE id=@kiosk; DELETE FROM identity.sellers WHERE id=@seller");
            cleanup.Parameters.AddWithValue("seller", seller); cleanup.Parameters.AddWithValue("kiosk", kiosk);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
