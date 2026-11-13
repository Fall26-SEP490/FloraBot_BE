using System.Security.Claims;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

internal sealed record AssignInventoryTask(Guid Id, Guid AssigneeId, Guid KioskId, Guid SlotId,
    string Action, Guid? ExpectedBouquetId, Guid? ProductId, string Instructions);
internal sealed record StaffInventoryOperation(string Operation, string? Reason);
internal sealed record StaffInventoryScope(Guid KioskId, Guid SlotId, string SlotCode, Guid SellerId,
    string Action, Guid? ExpectedBouquetId, Guid? ProductId, string Instructions, string Status,
    string? SessionStage, string? SessionSubstate);

internal static class StaffInventoryTasks
{
    public static void MapStaffInventoryTasks(this WebApplication app)
    {
        app.MapPost("/api/me/inventory-tasks", AssignAsync).RequireAuthorization("Seller").WithTags("KioskOps")
            .Produces<StaffTaskResult>();
        app.MapGet("/api/staff/tasks/{taskId:guid}/inventory-maintenance", ReadAsync)
            .RequireAuthorization("SellerStaff").WithTags("KioskOps").Produces<StaffInventoryScope>();
        app.MapPost("/api/staff/tasks/{taskId:guid}/inventory-maintenance/access", IssueAsync)
            .RequireAuthorization("SellerStaff").RequireRateLimiting("maintenance-code-issue").WithTags("KioskOps")
            .Produces<MaintenanceAccessIssued>();
        app.MapPost("/api/staff/tasks/{taskId:guid}/inventory-maintenance/open", OpenFromWebAsync)
            .RequireAuthorization("SellerStaff").RequireRateLimiting("maintenance-code-issue").WithTags("KioskOps")
            .Produces<SlotMaintenanceSessionResponse>().Produces(400).Produces(404).ProducesProblem(409);
    }

    private static async Task<IResult> AssignAsync(AssignInventoryTask input, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        if (!TryIdentity(http, out var actorId, out var sellerId)) return Results.Forbid();
        if (input.Id == Guid.Empty || input.AssigneeId == Guid.Empty || input.KioskId == Guid.Empty || input.SlotId == Guid.Empty
            || input.Action is not ("LOAD" or "REMOVE" or "REPLACE")
            || (input.Action == "LOAD") != (input.ExpectedBouquetId is null)
            || (input.Action == "REMOVE") != (input.ProductId is null)
            || input.Instructions?.Trim().Length is not (>= 10 and <= 500))
            return Results.BadRequest();

        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT 1 FROM kiosk_ops.slots s
            JOIN kiosk_ops.kiosks k ON k.id=s.kiosk_id
            WHERE s.id=@slot AND k.id=@kiosk AND s.zone='WALK_IN'
                AND s.current_seller_id=@seller AND k.seller_id=@seller
                AND s.bouquet_id IS NOT DISTINCT FROM @bouquet
                AND (@product IS NULL OR EXISTS (
                    SELECT 1 FROM catalog.flower_products p WHERE p.id=@product AND p.seller_id=@seller AND p.status='ACTIVE'))
            FOR SHARE OF k,s
            """, connection, transaction);
        command.Parameters.AddWithValue("slot", input.SlotId);
        command.Parameters.AddWithValue("kiosk", input.KioskId);
        command.Parameters.AddWithValue("seller", sellerId);
        command.Parameters.Add("bouquet", NpgsqlDbType.Uuid).Value = (object?)input.ExpectedBouquetId ?? DBNull.Value;
        command.Parameters.Add("product", NpgsqlDbType.Uuid).Value = (object?)input.ProductId ?? DBNull.Value;
        if (await command.ExecuteScalarAsync(ct) is null) return Results.NotFound();

        command.CommandText = """
            SELECT flow.assign_staff_task(@id,@actor,@staff,'DELIVERY',@kiosk,@seller,NULL,@instructions);
            INSERT INTO kiosk_ops.staff_inventory_tasks(task_id,slot_id,action,expected_bouquet_id,product_id)
                SELECT @id,@slot,@action,@bouquet,@product
                WHERE NOT EXISTS (SELECT 1 FROM kiosk_ops.staff_preorder_tasks WHERE task_id=@id)
                ON CONFLICT(task_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("id", input.Id);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("staff", input.AssigneeId);
        command.Parameters.AddWithValue("action", input.Action);
        command.Parameters.AddWithValue("instructions", input.Instructions.Trim());
        try
        {
            await command.ExecuteNonQueryAsync(ct);
            command.CommandText = """
                SELECT 1 FROM kiosk_ops.staff_inventory_tasks WHERE task_id=@id AND slot_id=@slot AND action=@action
                  AND expected_bouquet_id IS NOT DISTINCT FROM @bouquet AND product_id IS NOT DISTINCT FROM @product
                """;
            if (await command.ExecuteScalarAsync(ct) is null) return Results.Conflict();
            await transaction.CommitAsync(ct);
            return Results.Ok(new StaffTaskResult(input.Id));
        }
        catch (PostgresException ex) when (ex.SqlState is "42501" or "22023" or "40001" or "40P01")
        {
            return ex.SqlState switch
            {
                "42501" => Results.Forbid(),
                "22023" => Results.BadRequest(),
                _ => Results.Conflict()
            };
        }
    }

    private static async Task<IResult> ReadAsync(Guid taskId, HttpContext http, NpgsqlDataSource data, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryIdentity(http, out var actorId, out var sellerId)) return Results.Forbid();
        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var scope = await LoadAsync(connection, transaction, taskId, actorId, sellerId, ct);
        return scope is null ? Results.NotFound() : Results.Ok(scope);
    }

    private static async Task<IResult> IssueAsync(Guid taskId, StaffInventoryOperation input, HttpContext http,
        NpgsqlDataSource data, MaintenanceAccessStore store, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryIdentity(http, out var actorId, out var sellerId)) return Results.Forbid();
        if (input.Operation is not ("START" or "OPEN_NEXT" or "OPEN_FOR_CHECK")
            || (input.Operation == "OPEN_FOR_CHECK" && input.Reason?.Trim().Length is not (>= 5 and <= 500)))
            return Results.BadRequest();

        await using var connection = await data.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var scope = await LoadAsync(connection, transaction, taskId, actorId, sellerId, ct);
        if (scope is null) return Results.NotFound();
        if (scope.Status != "IN_PROGRESS"
            || (input.Operation == "START" && scope.SessionStage is not null)
            || (input.Operation == "OPEN_NEXT" && scope.SessionSubstate != "RemovalClosedEmptyConfirmed")
            || (input.Operation == "OPEN_FOR_CHECK" && scope.SessionStage != "NeedsInspection"))
            return Results.Conflict();
        var request = new MaintenanceAccessRequest(scope.KioskId, scope.SlotId, input.Operation, taskId,
            scope.Action, scope.ExpectedBouquetId, scope.ProductId,
            input.Operation == "OPEN_FOR_CHECK" ? input.Reason : scope.Instructions);
        var code = await store.IssueAsync(connection, transaction, new(scope.KioskId, scope.SlotId, sellerId,
            actorId, "SELLER_STAFF", taskId, input.Operation, JsonSerializer.Serialize(request)), ct);
        await transaction.CommitAsync(ct);
        return Results.Ok(code);
    }

    private static async Task<IResult> OpenFromWebAsync(Guid taskId, StaffInventoryOperation input, HttpContext http,
        NpgsqlDataSource data, SlotMaintenanceService service, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryIdentity(http, out var actorId, out var sellerId)) return Results.Forbid();
        if (input.Operation is not ("START" or "OPEN_NEXT" or "OPEN_FOR_CHECK")
            || (input.Operation == "OPEN_FOR_CHECK" && input.Reason?.Trim().Length is not (>= 5 and <= 500)))
            return Results.BadRequest();

        StaffInventoryScope? scope;
        await using (var connection = await data.OpenConnectionAsync(ct))
        await using (var transaction = await connection.BeginTransactionAsync(ct))
            scope = await LoadAsync(connection, transaction, taskId, actorId, sellerId, ct);

        if (scope is null) return Results.NotFound();
        if (scope.Status != "IN_PROGRESS") return Results.Conflict();
        var reason = input.Operation == "OPEN_FOR_CHECK" ? input.Reason : scope.Instructions;
        return await MaintenanceAccessEndpoints.OpenWalkInAsync(
            new(scope.KioskId, scope.SlotId, input.Operation, taskId, scope.Action,
                scope.ExpectedBouquetId, scope.ProductId, reason),
            actorId, sellerId, service, ct);
    }

    public static async Task<StaffInventoryScope?> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid taskId, Guid actorId, Guid sellerId, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT t.kiosk_id,i.slot_id,s.slot_code,t.seller_id,i.action,i.expected_bouquet_id,i.product_id,t.instructions,t.status,ms.stage,ms.substate
            FROM kiosk_ops.staff_tasks t
            JOIN kiosk_ops.staff_inventory_tasks i ON i.task_id=t.id
            JOIN kiosk_ops.slots s ON s.id=i.slot_id AND s.kiosk_id=t.kiosk_id AND s.current_seller_id=t.seller_id
            JOIN identity.users u ON u.id=t.assignee_id AND u.seller_id=t.seller_id
            LEFT JOIN kiosk_ops.slot_maintenance_sessions ms ON ms.id=t.id AND ms.actor_id=u.id
            WHERE t.id=@task AND t.kind='DELIVERY' AND t.assignee_id=@actor AND t.seller_id=@seller
              AND u.role='SELLER_STAFF' AND u.status='ACTIVE'
            FOR SHARE OF t,i,u
            """, connection, transaction);
        command.Parameters.AddWithValue("task", taskId);
        command.Parameters.AddWithValue("actor", actorId);
        command.Parameters.AddWithValue("seller", sellerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetGuid(6),
                reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10))
            : null;
    }

    private static bool TryIdentity(HttpContext http, out Guid actorId, out Guid sellerId) =>
        Guid.TryParse(http.User.FindFirstValue("sub"), out actorId)
        & Guid.TryParse(http.User.FindFirstValue("seller_id"), out sellerId);
}
