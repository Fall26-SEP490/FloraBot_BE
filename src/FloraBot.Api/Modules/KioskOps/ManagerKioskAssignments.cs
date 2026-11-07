using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record AssignKioskManagerRequest(Guid? ManagerId);
public sealed record KioskManagerAssignment(Guid KioskId, string KioskCode, Guid? ManagerId, string? ManagerName, DateTime? AssignedAt);

public static class ManagerKioskAssignments
{
    public static void MapManagerKioskAssignments(this WebApplication app)
    {
        app.MapGet("/api/admin/kiosk-manager-assignments", async (
            Guid? managerId, int? page, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            var number = page.GetValueOrDefault(1);
            if (number is < 1 or > 100000) return Results.BadRequest();
            await using (var active = data.CreateCommand("SELECT 1 FROM identity.users WHERE id=@actor AND role='ADMIN' AND status='ACTIVE'"))
            {
                active.Parameters.AddWithValue("actor", actor);
                if (await active.ExecuteScalarAsync(ct) is null) return Results.Forbid();
            }
            await using var command = data.CreateCommand("""
                SELECT k.id,k.code,m.manager_id,u.full_name,m.created_at
                FROM kiosk_ops.kiosks k
                LEFT JOIN kiosk_ops.manager_kiosks m ON m.kiosk_id=k.id
                LEFT JOIN identity.users u ON u.id=m.manager_id
                WHERE (@manager IS NULL OR m.manager_id=@manager)
                ORDER BY k.code,k.id
                LIMIT 51 OFFSET @skip
                """);
            command.Parameters.Add("manager", NpgsqlDbType.Uuid).Value = (object?)managerId ?? DBNull.Value;
            command.Parameters.AddWithValue("skip", (number - 1) * 50);
            var rows = new List<KioskManagerAssignment>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
            return Results.Ok(new { items = rows.Take(50), page = number, hasMore = rows.Count > 50 });
        }).RequireAuthorization("Admin").WithTags("KioskOps");

        app.MapPut("/api/admin/kiosks/{kioskId:guid}/operations-manager", async (
            Guid kioskId, AssignKioskManagerRequest input, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            if (input.ManagerId == Guid.Empty) return Results.BadRequest();

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            await using (var actorCheck = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE id=@actor AND role='ADMIN' AND status='ACTIVE' FOR SHARE", connection, tx))
            {
                actorCheck.Parameters.AddWithValue("actor", actor);
                if (await actorCheck.ExecuteScalarAsync(ct) is null) return Results.Forbid();
            }
            // All changes for a kiosk serialize on the kiosk row.
            string kioskCode;
            await using (var kiosk = new NpgsqlCommand("SELECT code FROM kiosk_ops.kiosks WHERE id=@id FOR UPDATE", connection, tx))
            {
                kiosk.Parameters.AddWithValue("id", kioskId);
                kioskCode = (string?)await kiosk.ExecuteScalarAsync(ct) ?? "";
                if (kioskCode.Length == 0) return Results.NotFound();
            }

            Guid? current = null;
            await using (var existing = new NpgsqlCommand("SELECT manager_id FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@id", connection, tx))
            {
                existing.Parameters.AddWithValue("id", kioskId);
                var value = await existing.ExecuteScalarAsync(ct);
                if (value is Guid id) current = id;
            }
            if (current == input.ManagerId)
            {
                await tx.CommitAsync(ct);
                return Results.Ok(new KioskManagerAssignment(kioskId, kioskCode, current, null, null));
            }
            if (current is not null)
            {
                await using var activeWork = new NpgsqlCommand("""
                    SELECT 1 FROM kiosk_ops.staff_tasks
                    WHERE kiosk_id=@kiosk AND kind='INCIDENT'
                      AND status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED')
                    LIMIT 1
                    """, connection, tx);
                activeWork.Parameters.AddWithValue("kiosk", kioskId);
                if (await activeWork.ExecuteScalarAsync(ct) is not null)
                    return Results.Problem(statusCode: 409, detail: "Kiosk còn công việc sự cố đang xử lý.");
            }
            string? managerName = null;
            if (input.ManagerId is Guid target)
            {
                await using var manager = new NpgsqlCommand("SELECT full_name FROM identity.users WHERE id=@id AND role='OPERATIONS_MANAGER' AND status='ACTIVE' FOR SHARE", connection, tx);
                manager.Parameters.AddWithValue("id", target);
                managerName = (string?)await manager.ExecuteScalarAsync(ct);
                if (managerName is null) return Results.Problem(statusCode: 400, detail: "Operations Manager không hoạt động hoặc không tồn tại.");
            }
            await using (var delete = new NpgsqlCommand("DELETE FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@id", connection, tx))
            {
                delete.Parameters.AddWithValue("id", kioskId);
                await delete.ExecuteNonQueryAsync(ct);
            }
            if (input.ManagerId is Guid newManager)
            {
                await using var insert = new NpgsqlCommand("INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id) VALUES(@manager,@kiosk)", connection, tx);
                insert.Parameters.AddWithValue("manager", newManager);
                insert.Parameters.AddWithValue("kiosk", kioskId);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await using (var audit = new NpgsqlCommand("SELECT flow.audit(@actor,'KIOSK_MANAGER_CHANGED','kiosks',@kiosk,jsonb_build_object('from',@old::text,'to',@new::text))", connection, tx))
            {
                audit.Parameters.AddWithValue("actor", actor);
                audit.Parameters.AddWithValue("kiosk", kioskId);
                audit.Parameters.Add("old", NpgsqlDbType.Text).Value = (object?)current?.ToString() ?? DBNull.Value;
                audit.Parameters.Add("new", NpgsqlDbType.Text).Value = (object?)input.ManagerId?.ToString() ?? DBNull.Value;
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return Results.Ok(new KioskManagerAssignment(kioskId, kioskCode, input.ManagerId, managerName, DateTime.UtcNow));
        }).RequireAuthorization("Admin").WithTags("KioskOps").Produces<KioskManagerAssignment>();
    }
}
