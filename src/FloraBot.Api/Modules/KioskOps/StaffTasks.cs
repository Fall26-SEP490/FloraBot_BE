using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Modules.Identity;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record AssignStaffTask(Guid Id, Guid AssigneeId, string Kind, Guid KioskId, Guid? SellerId, Guid? IncidentId, string Instructions);
public sealed record TransitionStaffTask(int Version, string Status, string Report);
public sealed record ReassignStaffTask(int Version, Guid AssigneeId, string Reason);
public sealed record StaffTaskResult(Guid Id, int? Version = null);
public sealed record StaffDeliveryScope(Guid SellerId, string ShopName, Guid KioskId, string KioskName, string Address);
public sealed record StaffTaskRow(Guid Id, string Kind, string Status, int Version, Guid AssigneeId, string AssigneeName,
    Guid KioskId, string KioskName, string Address, Guid? SellerId, string? ShopName, Guid? IncidentId,
    string Instructions, string? Report, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record StaffTaskPage(List<StaffTaskRow> Items, int Page, bool HasMore);

public static class StaffTasks
{
    public static void MapStaffTasks(this WebApplication app)
    {
        app.MapGet("/api/admin/staff-delivery-scopes", async (NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var sellers = await StaffIdentityRead.ActiveSellers(data, ct);
            await using var command = data.CreateCommand("""
                SELECT DISTINCT slot.current_seller_id,k.id,k.name,k.address
                FROM kiosk_ops.slots slot
                JOIN kiosk_ops.kiosks k ON k.id=slot.kiosk_id
                WHERE slot.current_seller_id=ANY(@sellers) AND k.status<>'DISABLED' AND slot.status NOT IN ('FREE','PENDING_RELEASE')
                ORDER BY k.name,slot.current_seller_id,k.id
                """);
            command.Parameters.AddWithValue("sellers", sellers.Keys.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<StaffDeliveryScope>();
            while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), sellers[reader.GetGuid(0)], reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
            return Results.Ok(rows);
        }).RequireAuthorization("Admin").Produces<List<StaffDeliveryScope>>();

        app.MapGet("/api/admin/staff", async (FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await StaffIdentityRead.ActiveStaff(db, ct));
        }).RequireAuthorization("Admin").Produces<List<StaffDirectoryItem>>();

        app.MapGet("/api/admin/staff-tasks", (int? page, string? status, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
            Read(page, status, true, data, http, ct)).RequireAuthorization("Admin").Produces<StaffTaskPage>();
        app.MapGet("/api/staff/tasks", (int? page, string? status, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
            Read(page, status, false, data, http, ct)).RequireAuthorization("Staff").Produces<StaffTaskPage>();

        app.MapPost("/api/admin/staff-tasks", async (AssignStaffTask input, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            if (input.Id == Guid.Empty || input.AssigneeId == Guid.Empty || input.KioskId == Guid.Empty ||
                input.Kind is not ("INCIDENT" or "DELIVERY") || !ValidReport(input.Instructions)) return Results.BadRequest();
            await using var command = data.CreateCommand("SELECT flow.assign_staff_task(@id,@actor,@staff,@kind,@kiosk,@seller,@incident,@instructions)");
            command.Parameters.AddWithValue("id", input.Id);
            command.Parameters.AddWithValue("actor", Actor(http));
            command.Parameters.AddWithValue("staff", input.AssigneeId);
            command.Parameters.AddWithValue("kind", input.Kind);
            command.Parameters.AddWithValue("kiosk", input.KioskId);
            command.Parameters.AddWithValue("seller", NpgsqlDbType.Uuid, (object?)input.SellerId ?? DBNull.Value);
            command.Parameters.AddWithValue("incident", NpgsqlDbType.Uuid, (object?)input.IncidentId ?? DBNull.Value);
            command.Parameters.AddWithValue("instructions", input.Instructions.Trim());
            try { await command.ExecuteScalarAsync(ct); return Results.Ok(new StaffTaskResult(input.Id)); }
            catch (PostgresException ex) when (Handled(ex)) { return Problem(ex); }
        }).RequireAuthorization("Admin").Produces<StaffTaskResult>();

        app.MapPost("/api/staff/tasks/{taskId:guid}/transition", Transition).RequireAuthorization("Staff").Produces<StaffTaskResult>();
        app.MapPost("/api/admin/staff-tasks/{taskId:guid}/transition", Transition).RequireAuthorization("Admin").Produces<StaffTaskResult>();
        app.MapPost("/api/admin/staff-tasks/{taskId:guid}/reassign", async (Guid taskId, ReassignStaffTask input, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            if (input.Version < 1 || input.AssigneeId == Guid.Empty || !ValidReport(input.Reason)) return Results.BadRequest();
            await using var command = data.CreateCommand("SELECT flow.reassign_staff_task(@id,@actor,@staff,@version,@reason)");
            command.Parameters.AddWithValue("id", taskId); command.Parameters.AddWithValue("actor", Actor(http));
            command.Parameters.AddWithValue("staff", input.AssigneeId); command.Parameters.AddWithValue("version", input.Version);
            command.Parameters.AddWithValue("reason", input.Reason.Trim());
            try { var version = (int)(await command.ExecuteScalarAsync(ct))!; return Results.Ok(new StaffTaskResult(taskId, version)); }
            catch (PostgresException ex) when (Handled(ex)) { return Problem(ex); }
        }).RequireAuthorization("Admin").Produces<StaffTaskResult>();
    }

    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue("sub")!);
    private static bool ValidReport(string? text) => text?.Trim() is { Length: >= 10 and <= 2000 } && !text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));
    private static bool Handled(PostgresException ex) => ex.SqlState is "42501" or "P0002" or "40001" or "22023" or "23505" or "23514" or "23503";
    private static IResult Problem(PostgresException ex) => ex.SqlState switch
    {
        "42501" => Results.Forbid(),
        "P0002" => Results.NotFound(),
        "40001" or "23505" => Results.Problem(statusCode: 409, detail: "Công việc đã thay đổi hoặc đã được phân công. Tải lại danh sách trước khi tiếp tục."),
        _ => Results.Problem(statusCode: 400, detail: "Kiểm tra nhân viên, tủ, shop/sự cố và nội dung công việc. Trạng thái hiện tại có thể chưa cho phép thao tác này.")
    };

    private static async Task<IResult> Transition(Guid taskId, TransitionStaffTask input, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        if (input.Version < 1 || !ValidReport(input.Report) || input.Status is not ("IN_PROGRESS" or "SUBMITTED" or "COMPLETED" or "CANCELLED")) return Results.BadRequest();
        await using var command = data.CreateCommand("SELECT flow.transition_staff_task(@id,@actor,@version,@status,@report)");
        command.Parameters.AddWithValue("id", taskId); command.Parameters.AddWithValue("actor", Actor(http));
        command.Parameters.AddWithValue("version", input.Version); command.Parameters.AddWithValue("status", input.Status);
        command.Parameters.AddWithValue("report", input.Report.Trim());
        try { var version = (int)(await command.ExecuteScalarAsync(ct))!; return Results.Ok(new StaffTaskResult(taskId, version)); }
        catch (PostgresException ex) when (Handled(ex)) { return Problem(ex); }
    }

    private static async Task<IResult> Read(int? page, string? status, bool admin, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var number = page ?? 1;
        if (number is < 1 or > 100000 || status is not (null or "ASSIGNED" or "IN_PROGRESS" or "SUBMITTED" or "COMPLETED" or "CANCELLED")) return Results.BadRequest();
        await using var command = data.CreateCommand("""
            SELECT t.id,t.kind,t.status,t.version,t.assignee_id,''::text,t.kiosk_id,k.name,k.address,
              t.seller_id,NULL::text,t.incident_id,t.instructions,t.report,t.created_at,t.updated_at
            FROM kiosk_ops.staff_tasks t
            JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
            WHERE (@admin OR t.assignee_id=@actor) AND (@status IS NULL OR t.status=@status)
            ORDER BY t.created_at DESC,t.id DESC LIMIT 26 OFFSET @offset
            """);
        command.Parameters.AddWithValue("admin", admin); command.Parameters.AddWithValue("actor", Actor(http));
        command.Parameters.AddWithValue("status", NpgsqlDbType.Text, (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("offset", (number - 1) * 25);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<StaffTaskRow>();
        while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetGuid(4), reader.GetString(5), reader.GetGuid(6), reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetGuid(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13), reader.GetDateTime(14), reader.GetDateTime(15)));
        await reader.DisposeAsync();
        var users = await StaffIdentityRead.Users(data, items.Select(x => x.AssigneeId).Distinct().ToArray(), ct);
        var shops = await StaffIdentityRead.Sellers(data, items.Where(x => x.SellerId.HasValue).Select(x => x.SellerId!.Value).Distinct().ToArray(), ct);
        var visible = items.Take(25).Select(x => x with { AssigneeName = users.GetValueOrDefault(x.AssigneeId, "Nhan vien"), ShopName = x.SellerId is Guid id ? shops.GetValueOrDefault(id) : null }).ToList();
        return Results.Ok(new StaffTaskPage(visible, number, items.Count > 25));
    }
}
