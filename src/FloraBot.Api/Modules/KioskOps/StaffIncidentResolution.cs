using System.Security.Claims;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record StaffIncidentResolutionInput(Guid Id, int Version, string Report);
public sealed record StaffIncidentResolutionResult(Guid Id, int Version);
public static class StaffIncidentResolution
{
    public static void MapStaffIncidentResolution(this WebApplication app)
    {
        app.MapPost("/api/staff/tasks/{taskId:guid}/resolve-incident", async (Guid taskId, StaffIncidentResolutionInput input, NpgsqlDataSource data, ICapPublisher publisher, HttpContext http, CancellationToken ct) =>
        {
            if (input.Id == Guid.Empty || input.Version < 1 || string.IsNullOrWhiteSpace(input.Report) || input.Report.Trim().Length is < 10 or > 2000) return Results.BadRequest();
            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
            try
            {
                var watermark = await TransactionChanges.WatermarkAsync(connection, transaction, ct);
                await using var command = new NpgsqlCommand("SELECT flow.staff_resolve_incident(@id,@task,@actor,@version,@report)", connection, transaction);
                command.Parameters.AddWithValue("id", input.Id); command.Parameters.AddWithValue("task", taskId);
                command.Parameters.AddWithValue("actor", Guid.Parse(http.User.FindFirstValue("sub")!));
                command.Parameters.AddWithValue("version", input.Version); command.Parameters.AddWithValue("report", input.Report.Trim());
                var version = (int)(await command.ExecuteScalarAsync(ct))!;
                await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
                await outbox.CommitAsync(ct);
                return Results.Ok(new StaffIncidentResolutionResult(input.Id, version));
            }
            catch (PostgresException ex) when (ex.SqlState is "42501" or "P0002" or "40001" or "22023" or "23505" or "P0001")
            {
                return ex.SqlState switch
                {
                    "42501" => Results.Forbid(),
                    "P0002" => Results.NotFound(),
                    "40001" or "23505" => Results.Problem(statusCode: 409, detail: "Công việc hoặc sự cố đã thay đổi. Tải lại để kiểm tra kết quả trước khi tiếp tục."),
                    _ => Results.Problem(statusCode: 400, detail: "Chưa thể đóng sự cố. Kiểm tra báo cáo và khoản hoàn tiền đang chờ; liên hệ quản trị viên để xử lý.")
                };
            }
        }).RequireAuthorization("Technician").Produces<StaffIncidentResolutionResult>();
    }
}
