using System.Data;
using System.Security.Claims;
using FloraBot.Api.Modules.Ordering;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record TechnicianSlotTelemetry(string SlotCode, bool? FlowerDetected, bool? DoorClosed,
    string? LastCycle, DateTimeOffset? LastCycleAt);

public sealed record TechnicianTaskTelemetry(string KioskStatus, DateTime? LastHeartbeatAt,
    DateTimeOffset? ObservedAt, bool IsFresh, List<TechnicianSlotTelemetry> Slots);

public sealed record TechnicianKioskHealthSummary(Guid Id, string Code, string Name, string Status);

public static class TechnicianTaskTelemetryEndpoints
{
    private const string AssignedKiosk = """
        (EXISTS (
            SELECT 1 FROM kiosk_ops.staff_tasks t
            WHERE t.kiosk_id=k.id AND t.assignee_id=@actor AND t.kind IN ('INCIDENT','KIOSK_REMOVAL')
              AND t.status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED')
        ) OR EXISTS (
            SELECT 1 FROM kiosk_ops.kiosk_applications a
            WHERE a.assigned_kiosk_id=k.id AND a.assigned_technician_id=@actor
              AND (a.status IN ('SCHEDULED','INSTALLING','AWAITING_HANDOVER')
                   OR (a.status='ACTIVE' AND a.seller_id=k.seller_id))
        ))
        """;

    public static void MapTechnicianTaskTelemetry(this WebApplication app)
    {
        app.MapGet("/api/staff/tasks/{taskId:guid}/iot-health", ReadTask)
            .RequireAuthorization("Technician").WithTags("KioskOps").Produces<TechnicianTaskTelemetry>()
            .WithSummary("Latest hardware signals for the technician's assigned work");
        var health = app.MapGroup("/api/staff/kiosk-health").RequireAuthorization("Technician").WithTags("KioskOps");
        health.MapGet("", ListKiosks).Produces<List<TechnicianKioskHealthSummary>>()
            .WithSummary("Kiosks assigned to the technician for installation or repair");
        health.MapGet("/{kioskId:guid}", ReadKiosk).Produces<TechnicianTaskTelemetry>()
            .WithSummary("Latest hardware signals from all compartments of an assigned kiosk");
    }

    private static async Task<IResult> ListKiosks(NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Forbid();
        await using var command = data.CreateCommand($"""
            SELECT k.id,k.code,k.name,k.status FROM kiosk_ops.kiosks k
            WHERE {AssignedKiosk}
            ORDER BY k.code,k.id
            """);
        command.Parameters.AddWithValue("actor", actor);
        var kiosks = new List<TechnicianKioskHealthSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            kiosks.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return Results.Ok(kiosks);
    }

    private static async Task<IResult> ReadKiosk(Guid kioskId, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Forbid();
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var scope = new NpgsqlCommand(
            $"SELECT 1 FROM kiosk_ops.kiosks k WHERE k.id=@kiosk AND {AssignedKiosk}", connection, transaction);
        scope.Parameters.AddWithValue("actor", actor);
        scope.Parameters.AddWithValue("kiosk", kioskId);
        if (await scope.ExecuteScalarAsync(ct) is null) return Results.NotFound();
        var telemetry = await ReadTelemetry(connection, transaction, kioskId, null, ct);
        await transaction.CommitAsync(ct);
        return telemetry is null ? Results.NotFound() : Results.Ok(telemetry);
    }

    private static async Task<IResult> ReadTask(Guid taskId, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        if (await StaffTaskAccess.Lock(connection, transaction, taskId, http, ct, readOnly: true) is null)
            return Results.NotFound();

        Guid kiosk;
        Guid? incidentId;
        await using (var command = new NpgsqlCommand(
            "SELECT kiosk_id,incident_id FROM kiosk_ops.staff_tasks WHERE id=@task", connection, transaction))
        {
            command.Parameters.AddWithValue("task", taskId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return Results.NotFound();
            kiosk = reader.GetGuid(0);
            incidentId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        }

        Guid? slot = null;
        if (incidentId is Guid id)
        {
            var incident = await StaffIncidentContext.Read(connection, transaction, id, kiosk, ct);
            if (incident is null) return Results.NotFound();
            slot = incident.SlotId;
        }
        var telemetry = await ReadTelemetry(connection, transaction, kiosk, slot, ct);
        await transaction.CommitAsync(ct);
        return telemetry is null ? Results.NotFound() : Results.Ok(telemetry);
    }

    private static async Task<TechnicianTaskTelemetry?> ReadTelemetry(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid kiosk, Guid? slot, CancellationToken ct)
    {
        string kioskStatus;
        DateTime? heartbeat;
        DateTimeOffset? observedAt;
        bool fresh;
        int[] flowers, doors, cycles;
        long[] cycleTimes;
        await using (var command = new NpgsqlCommand("""
            SELECT k.status,k.last_heartbeat_at,p.observed_at,p.states,
                   p.door_states,p.door_cycles,p.door_cycle_times,
                   COALESCE(k.status='ONLINE'
                     AND p.received_at > CURRENT_TIMESTAMP - INTERVAL '45 seconds'
                     AND p.observed_at > extract(epoch FROM CURRENT_TIMESTAMP)*1000 - 45000
                     AND p.observed_at <= extract(epoch FROM CURRENT_TIMESTAMP)*1000 + 5000, false)
            FROM kiosk_ops.kiosks k
            LEFT JOIN kiosk_ops.flower_presence p ON p.kiosk_id=k.id
            WHERE k.id=@kiosk
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("kiosk", kiosk);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            kioskStatus = reader.GetString(0);
            heartbeat = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            observedAt = reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            flowers = reader.IsDBNull(3) ? [] : reader.GetFieldValue<int[]>(3);
            doors = reader.IsDBNull(4) ? [] : reader.GetFieldValue<int[]>(4);
            cycles = reader.IsDBNull(5) ? [] : reader.GetFieldValue<int[]>(5);
            cycleTimes = reader.IsDBNull(6) ? [] : reader.GetFieldValue<long[]>(6);
            fresh = reader.GetBoolean(7);
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
        return new(kioskStatus, heartbeat, observedAt, fresh, slots);
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
