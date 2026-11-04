using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record SignedFlowerPresence(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("hardware_id")] string HardwareId,
    [property: JsonPropertyName("event_id")] Guid EventId,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("occurred_at")] long OccurredAt,
    [property: JsonPropertyName("states")] int[] States,
    [property: JsonPropertyName("signature")] string Signature);

public static class FlowerPresenceProtocol
{
    public const string Event = "FLOWER_PRESENCE";
    public static bool Valid(SignedFlowerPresence m) => m.Version == 1 && m.Event == Event
        && DeviceProtocol.ValidHardwareId(m.HardwareId) && m.EventId != Guid.Empty && m.OccurredAt > 0
        && m.States is { Length: 4 } && m.States.All(s => s is >= -1 and <= 1);

    public static string Canonical(SignedFlowerPresence m) => string.Join('\n',
        new[] { "florabot.flower.v1", m.HardwareId, m.EventId.ToString("D"), m.OccurredAt.ToString(CultureInfo.InvariantCulture) }
            .Concat(m.States.Select(s => s.ToString(CultureInfo.InvariantCulture))));

    public static SignedFlowerPresence Sign(SignedFlowerPresence m, byte[] key)
    {
        if (!Valid(m) || key.Length != 32) throw new ArgumentException("Invalid flower telemetry or key.");
        return m with { Signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(Canonical(m)))) };
    }

    public static bool Verify(SignedFlowerPresence m, string hardware, byte[] key, DateTimeOffset now)
    {
        if (!Valid(m) || m.HardwareId != hardware || key.Length != 32
            || m.OccurredAt < now.ToUnixTimeMilliseconds() - 120000 || m.OccurredAt > now.ToUnixTimeMilliseconds() + 5000
            || m.Signature is not { Length: 64 } || !m.Signature.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(m.Signature),
            Convert.FromHexString(Sign(m, key).Signature));
    }
}

// Observations only: no stock, unlock-token, order or payment transitions.
public sealed class FlowerPresenceReceiver(NpgsqlDataSource data, DeviceKeyRing keys)
{
    public async Task<bool> ReceiveAsync(string hardware, SignedFlowerPresence message, CancellationToken ct)
    {
        if (!keys.Verify(message, hardware, DateTimeOffset.UtcNow)) return false;
        await using var command = data.CreateCommand("""
            INSERT INTO kiosk_ops.flower_presence(kiosk_id,event_id,observed_at,states,received_at)
            SELECT k.id,@event,@at,@states,CURRENT_TIMESTAMP FROM kiosk_ops.kiosks k
            WHERE k.hardware_id=@hardware AND k.status<>'DISABLED'
            ON CONFLICT(kiosk_id) DO UPDATE SET event_id=excluded.event_id,observed_at=excluded.observed_at,
              states=excluded.states,received_at=excluded.received_at
            WHERE kiosk_ops.flower_presence.observed_at<excluded.observed_at
            """);
        command.Parameters.AddWithValue("event", message.EventId);
        command.Parameters.AddWithValue("at", message.OccurredAt);
        command.Parameters.AddWithValue("states", NpgsqlDbType.Array | NpgsqlDbType.Integer, message.States);
        command.Parameters.AddWithValue("hardware", hardware);
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }
}

public sealed record FlowerSlotPresence(Guid SlotId, string SlotCode, int RelayChannel, bool? HasFlowers);
public sealed record FlowerPresenceResponse(Guid KioskId, DateTimeOffset? ObservedAt, DateTime? ReceivedAt,
    bool IsFresh, List<FlowerSlotPresence> Slots);

public static class FlowerPresenceEndpoints
{
    public static void MapFlowerPresence(this WebApplication app)
    {
        app.MapGet("/api/kiosks/{id:guid}/flower-presence", async (Guid id, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (http.User.HasClaim(c => c.Type == "kiosk_id")) return Results.Forbid();
            var privileged = http.User.IsInRole("ADMIN") || http.User.IsInRole("OPERATIONS_MANAGER");
            var seller = http.User.IsInRole("SELLER") && Guid.TryParse(http.User.FindFirstValue("seller_id"), out _)
                ? Guid.Parse(http.User.FindFirstValue("seller_id")!) : Guid.Empty;
            var actor = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub"), out var user) ? user : Guid.Empty;
            await using var command = data.CreateCommand("""
                SELECT s.id,s.slot_code,s.relay_channel,p.observed_at,p.received_at,p.states,
                  k.status='ONLINE' AND p.received_at>CURRENT_TIMESTAMP-interval '45 seconds'
                    AND p.observed_at>extract(epoch FROM CURRENT_TIMESTAMP)*1000-45000 AS fresh
                FROM kiosk_ops.kiosks k JOIN kiosk_ops.slots s ON s.kiosk_id=k.id
                LEFT JOIN kiosk_ops.flower_presence p ON p.kiosk_id=k.id
                WHERE k.id=@id AND (@privileged OR k.seller_id=@seller OR (@technician AND EXISTS (
                  SELECT 1 FROM kiosk_ops.kiosk_applications a WHERE a.assigned_kiosk_id=k.id AND a.assigned_technician_id=@actor)))
                ORDER BY s.relay_channel,s.slot_code
                """);
            command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("privileged", privileged);
            command.Parameters.AddWithValue("seller", seller); command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("technician", http.User.IsInRole("TECHNICIAN"));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var slots = new List<FlowerSlotPresence>();
            long? observed = null; DateTime? received = null; bool fresh = false;
            while (await reader.ReadAsync(ct))
            {
                var relay = Convert.ToInt32(reader.GetValue(2));
                observed = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                received = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                fresh = !reader.IsDBNull(6) && reader.GetBoolean(6);
                var states = reader.IsDBNull(5) ? [] : reader.GetFieldValue<int[]>(5);
                bool? hasFlowers = fresh && relay >= 0 && relay < states.Length && states[relay] != -1 ? states[relay] == 1 : null;
                slots.Add(new(reader.GetGuid(0), reader.GetString(1), relay, hasFlowers));
            }
            return slots.Count == 0 ? Results.NotFound() : Results.Ok(new FlowerPresenceResponse(id,
                observed.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(observed.Value) : null, received, fresh, slots));
        }).RequireAuthorization("Account").WithTags("KioskOps").Produces<FlowerPresenceResponse>()
          .WithSummary("Tín hiệu cảm biến hoa theo ô, chưa xác nhận hoàn tất lấy hàng");
    }
}
