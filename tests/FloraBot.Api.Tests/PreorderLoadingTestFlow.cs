using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace FloraBot.Api.Tests;

internal static class PreorderLoadingTestFlow
{
    // Simulates device telemetry around the authenticated web OTP/loading HTTP endpoints.
    // This validates the backend flow, not physical hardware operation.
    internal static async Task LoadAsync(NpgsqlDataSource data, ApiFactory factory, HttpClient actor,
        HttpClient device, Guid request, Guid kiosk, Guid slot, Guid actorId, int relay,
        string accessPath, string fulfillPath)
    {
        async Task<object?> Sql(string sql, params (string Key, object Value)[] args)
        {
            await using var cmd = data.CreateCommand(sql);
            cmd.Parameters.AddWithValue("kiosk", kiosk); cmd.Parameters.AddWithValue("slot", slot);
            cmd.Parameters.AddWithValue("request", request); cmd.Parameters.AddWithValue("relay", relay);
            foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value);
            return await cmd.ExecuteScalarAsync();
        }
        await Sql("""
            INSERT INTO kiosk_ops.slot_sensor_capabilities(slot_id,kiosk_id,relay_channel,enabled,door_verified,presence_verified)
            VALUES(@slot,@kiosk,@relay,true,true,true) ON CONFLICT(slot_id) DO UPDATE SET enabled=true,door_verified=true,presence_verified=true;
            UPDATE kiosk_ops.kiosks SET last_heartbeat_at=clock_timestamp(),status='ONLINE' WHERE id=@kiosk;
            INSERT INTO kiosk_ops.flower_presence(kiosk_id,event_id,observed_at,states,received_at)
            VALUES(@kiosk,gen_random_uuid(),(extract(epoch FROM clock_timestamp())*1000)::bigint,ARRAY[0,0,0,0],clock_timestamp())
            ON CONFLICT(kiosk_id) DO UPDATE SET states=excluded.states,observed_at=excluded.observed_at,received_at=excluded.received_at;
            """);
        var access = await actor.PostAsJsonAsync(accessPath, new { slotId = slot });
        Assert.True(access.IsSuccessStatusCode, await access.Content.ReadAsStringAsync());
        var accessId = (await access.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var code = factory.EmailSender.ExtractLastCode(actorId + "@example.invalid");
        Assert.NotNull(code);
        var confirmPath = $"/api/me/maintenance-access/{accessId}/confirm";
        Assert.Equal(HttpStatusCode.Forbidden, (await device.PostAsJsonAsync(confirmPath, new { code })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await actor.PostAsJsonAsync(confirmPath,
            new { code = code == "000000" ? "000001" : "000000" })).StatusCode);
        var confirmation = await actor.PostAsJsonAsync(confirmPath, new { code });
        Assert.True(confirmation.IsSuccessStatusCode, await confirmation.Content.ReadAsStringAsync());
        var confirmed = await confirmation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(confirmed.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.NotFound, (await actor.PostAsJsonAsync(confirmPath, new { code })).StatusCode);
        Assert.Equal(1L, await Sql("SELECT count(*) FROM kiosk_ops.preorder_loading_sessions WHERE challenge_id=@access", ("access", accessId)));
        var body = new { slotId = slot, qrCode = "loaded-preorder-" + request, loadingAccessId = accessId };
        Assert.Equal(HttpStatusCode.Conflict, (await actor.PostAsJsonAsync(fulfillPath, body)).StatusCode);
        await Sql("""
            SELECT flow.device_event((SELECT command_id FROM kiosk_ops.preorder_loading_sessions WHERE challenge_id=@access),'SENT');
            SELECT flow.device_event((SELECT command_id FROM kiosk_ops.preorder_loading_sessions WHERE challenge_id=@access),'ACK');
            SELECT flow.device_event((SELECT command_id FROM kiosk_ops.preorder_loading_sessions WHERE challenge_id=@access),'OPENED');
            SELECT flow.device_event((SELECT command_id FROM kiosk_ops.preorder_loading_sessions WHERE challenge_id=@access),'CLOSED');
            INSERT INTO kiosk_ops.mqtt_inbox(event_id,kiosk_id,cmd_id,event,payload_hash,occurred_at,processed_at,disposition)
            SELECT gen_random_uuid(),@kiosk,command_id,'ACK','preorder-loading-integration',clock_timestamp(),clock_timestamp(),'APPLIED'
            FROM kiosk_ops.preorder_loading_sessions WHERE challenge_id=@access;
            UPDATE kiosk_ops.flower_presence SET states[@relay+1]=1,received_at=clock_timestamp(),
              observed_at=(extract(epoch FROM clock_timestamp())*1000)::bigint+10 WHERE kiosk_id=@kiosk;
            """, ("access", accessId));
        var loaded = await actor.PostAsJsonAsync(fulfillPath, body);
        Assert.True(loaded.IsSuccessStatusCode, await loaded.Content.ReadAsStringAsync());
        Assert.True((await actor.PostAsJsonAsync(fulfillPath, body)).IsSuccessStatusCode);
    }
}
