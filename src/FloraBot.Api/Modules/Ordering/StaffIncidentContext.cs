using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public sealed record StaffIncidentInfo(string Kind, string Status, string Reason, string? Decision, Guid? SlotId, DateTime CreatedAt);
public static class StaffIncidentContext
{
    public static async Task<StaffIncidentInfo?> Read(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid incidentId, Guid kioskId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT kind,status,reason,decision,slot_id,created_at FROM ordering.disputes
            WHERE id=@id AND kiosk_id=@kiosk AND kind IN ('DEVICE_FAULT','DISPENSE_FAILED')
            """, connection, transaction);
        command.Parameters.AddWithValue("id", incidentId); command.Parameters.AddWithValue("kiosk", kioskId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetDateTime(5)) : null;
    }
}
