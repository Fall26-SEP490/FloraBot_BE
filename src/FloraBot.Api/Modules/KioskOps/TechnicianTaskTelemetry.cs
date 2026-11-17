using System.Data;
using FloraBot.Api.Modules.Ordering;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record TechnicianSlotTelemetry(string SlotCode, bool? FlowerDetected, bool? DoorClosed,
    string? LastCycle, DateTimeOffset? LastCycleAt);

public sealed record TechnicianTaskTelemetry(string KioskStatus, DateTime? LastHeartbeatAt,
    DateTimeOffset? ObservedAt, bool IsFresh, List<TechnicianSlotTelemetry> Slots);

public static class TechnicianTaskTelemetryEndpoints
{
    public static void MapTechnicianTaskTelemetry(this WebApplication app) => app
        .MapGet("/api/staff/tasks/{taskId:guid}/iot-health", Read)
        .RequireAuthorization("Technician").WithTags("KioskOps").Produces<TechnicianTaskTelemetry>()
        .WithSummary("Latest hardware signals for the technician's assigned work");

    private static async Task<IResult> Read(Guid taskId, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct, readOnly: true) is null)
            return Results.NotFound();

        Guid kiosk;
        Guid? incidentId;
        string kioskStatus;
        DateTime? heartbeat;
        DateTimeOffset? observedAt;
        bool fresh;
        int[] flowers, doors, cycles;
        long[] cycleTimes;
        await using (var command = new NpgsqlCommand("""
            SELECT t.kiosk_id,t.incident_id,k.status,k.last_heartbeat_at,p.observed_at,p.states,
                   p.door_states,p.door_cycles,p.door_cycle_times,
                   COALESCE(k.status='ONLINE'
                     AND p.received_at > CURRENT_TIMESTAMP - INTERVAL '45 seconds'
                     AND p.observed_at > extract(epoch FROM CURRENT_TIMESTAMP)*1000 - 45000
                     AND p.observed_at <= extract(epoch FROM CURRENT_TIMESTAMP)*1000 + 5000, false)
            FROM kiosk_ops.staff_tasks t JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
            LEFT JOIN kiosk_ops.flower_presence p ON p.kiosk_id=k.id
            WHERE t.id=@task
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("task", taskId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            kiosk = reader.GetGuid(0);
            incidentId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
            kioskStatus = reader.GetString(2);
            heartbeat = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            observedAt = reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
            flowers = reader.IsDBNull(5) ? [] : reader.GetFieldValue<int[]>(5);
            doors = reader.IsDBNull(6) ? [] : reader.GetFieldValue<int[]>(6);
            cycles = reader.IsDBNull(7) ? [] : reader.GetFieldValue<int[]>(7);
            cycleTimes = reader.IsDBNull(8) ? [] : reader.GetFieldValue<long[]>(8);
            fresh = reader.GetBoolean(9);
        }

        Guid? slot = null;
        if (incidentId is Guid id)
        {
            var incident = await StaffIncidentContext.Read(connection, transaction, id, kiosk, ct);
            if (incident is null) return Results.NotFound();
            slot = incident.SlotId;
        }

        var slots = new List<TechnicianSlotTelemetry>();
        await using (var command = new NpgsqlCommand("""
            SELECT slot_code,relay_channel FROM kiosk_ops.slots
            WHERE kiosk_id=@kiosk AND (@slot IS NULL OR id=@slot)
            ORDER BY relay_channel,slot_code
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("kiosk", kiosk);
            command.Parameters.AddWithValue("slot", NpgsqlDbType.Uuid, (object?)slot ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var relay = Convert.ToInt32(reader.GetValue(1));
                var cycle = fresh ? ReadAt(cycles, relay) : null;
                var time = fresh ? ReadAt(cycleTimes, relay) : null;
                slots.Add(new(reader.GetString(0), fresh ? Signal(ReadAt(flowers, relay)) : null,
                    fresh ? Signal(ReadAt(doors, relay)) : null, CycleName(cycle),
                    time is > 0 ? DateTimeOffset.FromUnixTimeSeconds(time.Value) : null));
            }
        }
        await transaction.CommitAsync(ct);
        return Results.Ok(new TechnicianTaskTelemetry(kioskStatus, heartbeat, observedAt, fresh, slots));
    }

    private static T? ReadAt<T>(T[] values, int index) where T : struct =>
        index >= 0 && index < values.Length ? values[index] : null;

    private static bool? Signal(int? value) => value switch { 0 => false, 1 => true, _ => null };

    private static string? CycleName(int? value) => value switch
    {
        0 => "NONE",
        1 => "RELAY_PULSED",
        2 => "OPEN_DETECTED",
        3 => "CLOSE_DETECTED",
        4 => "OPEN_TIMEOUT",
        5 => "CLOSE_TIMEOUT",
        6 => "INSPECTION_REQUIRED",
        _ => null
    };
}
