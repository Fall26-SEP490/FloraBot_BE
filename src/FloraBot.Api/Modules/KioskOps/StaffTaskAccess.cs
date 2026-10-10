using System.Security.Claims;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record StaffTaskScope(string Status, int Version);
public static class StaffTaskAccess
{
    public static async Task<StaffTaskScope?> Lock(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid taskId, HttpContext http, CancellationToken ct, bool readOnly = false)
    {
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return null;
        var role = http.User.FindFirstValue("role");
        Guid? userSellerId = Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sid) ? sid : null;

        var sql = "SELECT status,version,kind,kiosk_id,seller_id,assignee_id FROM kiosk_ops.staff_tasks WHERE id=@id"
            + (readOnly ? " FOR SHARE OF staff_tasks" : " FOR UPDATE OF staff_tasks");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", taskId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var status = reader.GetString(0);
        var version = reader.GetInt32(1);
        var kind = reader.GetString(2);
        var kioskId = reader.GetGuid(3);
        var taskSellerId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
        var assigneeId = reader.GetGuid(5);
        await reader.DisposeAsync();

        if (role == "ADMIN")
            return new(status, version);

        if (role == "OPERATIONS_MANAGER")
        {
            if (kind != "INCIDENT") return null;
            await using var check = new NpgsqlCommand(
                "SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id=@mgr AND kiosk_id=@kiosk",
                connection, transaction);
            check.Parameters.AddWithValue("mgr", actor);
            check.Parameters.AddWithValue("kiosk", kioskId);
            return await check.ExecuteScalarAsync(ct) is not null ? new(status, version) : null;
        }

        if (role == "SELLER")
        {
            if (kind != "DELIVERY" || taskSellerId is null || taskSellerId != userSellerId) return null;
            return new(status, version);
        }

        if (role == "TECHNICIAN")
        {
            if (kind != "INCIDENT" || assigneeId != actor) return null;
            return new(status, version);
        }

        if (role == "SELLER_STAFF")
        {
            if (kind != "DELIVERY" || assigneeId != actor || taskSellerId is null || taskSellerId != userSellerId) return null;
            return new(status, version);
        }

        return null;
    }

    public static async Task<List<Guid>> ManagerKiosks(NpgsqlDataSource data, Guid managerId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT kiosk_id FROM kiosk_ops.manager_kiosks WHERE manager_id=@mgr");
        command.Parameters.AddWithValue("mgr", managerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<Guid>();
        while (await reader.ReadAsync(ct)) list.Add(reader.GetGuid(0));
        return list;
    }
}
