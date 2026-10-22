using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Notify;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record OperationsSellerSummary(
    Guid Id,
    string ShopName,
    string Phone,
    string? Address,
    string Status,
    Guid? PackageId,
    string? PackageName,
    DateOnly? PackageExpiresAt,
    DateTime? PackageExpiresAtTs,
    string? ContactEmail,
    string? BrandTone,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record OperationsSellerPage(
    List<OperationsSellerSummary> Items,
    int Page,
    bool HasMore);

public sealed record OperationsSellerDetail(
    Guid Id,
    string ShopName,
    string Phone,
    string? Address,
    string Status,
    Guid? PackageId,
    string? PackageName,
    DateOnly? PackageExpiresAt,
    DateTime? PackageExpiresAtTs,
    string? ContactEmail,
    string? BrandTone,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UpdateSellerStatusRequest(
    string Status,
    string Reason);

public static class OperationsSellerEndpoints
{
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "PENDING", "APPROVED", "ACTIVE", "PAST_DUE", "SUSPENDED", "CLOSED"
    };

    public static void MapOperationsSellers(this WebApplication app)
    {
        // 1. GET /api/operations/sellers
        app.MapGet("/api/operations/sellers", async (
            string? status,
            string? search,
            int? page,
            int? pageSize,
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            var pageNumber = page ?? 1;
            var size = pageSize ?? 25;
            if (pageNumber < 1 || pageNumber > 100000 || size < 1 || size > 100)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Tham số phân trang không hợp lệ.");

            var query = db.Sellers.AsNoTracking().Include(x => x.Package).AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
            {
                var upperStatus = status.Trim().ToUpperInvariant();
                if (!ValidStatuses.Contains(upperStatus))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Trạng thái không hợp lệ.");
                query = query.Where(x => x.Status == upperStatus);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                if (term.Length > 100)
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Từ khóa tìm kiếm tối đa 100 ký tự.");
                query = query.Where(x => EF.Functions.ILike(x.ShopName, $"%{term}%") || EF.Functions.ILike(x.Phone, $"%{term}%"));
            }

            query = query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);
            var rows = await query.Skip((pageNumber - 1) * size).Take(size + 1).ToListAsync(ct);
            var items = rows.Take(size).Select(MapToSummary).ToList();

            return Results.Ok(new OperationsSellerPage(items, pageNumber, rows.Count > size));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Danh sách shop bán hoa cho Operations Manager quản lý, có phân trang và lọc")
        .Produces<OperationsSellerPage>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        // 2. GET /api/operations/sellers/{sellerId}
        app.MapGet("/api/operations/sellers/{sellerId:guid}", async (
            Guid sellerId,
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            var seller = await db.Sellers.AsNoTracking()
                .Include(x => x.Package)
                .SingleOrDefaultAsync(x => x.Id == sellerId, ct);

            if (seller is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Shop không tồn tại.");

            return Results.Ok(MapToDetail(seller));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Chi tiết thông tin an toàn của một shop cho Operations Manager")
        .Produces<OperationsSellerDetail>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // 3. PATCH /api/operations/sellers/{sellerId}/status
        app.MapPatch("/api/operations/sellers/{sellerId:guid}/status", async (
            Guid sellerId,
            UpdateSellerStatusRequest request,
            NpgsqlDataSource data,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var managerId))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Reason))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Lý do là bắt buộc.");

            var reason = request.Reason.Trim();
            if (reason.Length is < 5 or > 1000)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Lý do phải từ 5 đến 1000 ký tự.");

            if (string.IsNullOrWhiteSpace(request.Status))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Trạng thái là bắt buộc.");

            var targetStatus = request.Status.Trim().ToUpperInvariant();
            if (targetStatus is not ("ACTIVE" or "SUSPENDED"))
            {
                if (targetStatus is "PENDING" or "APPROVED" or "PAST_DUE" or "CLOSED")
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: $"Không thể chuyển trạng thái sang {targetStatus}.");
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Trạng thái không hợp lệ. Chỉ chấp nhận ACTIVE hoặc SUSPENDED.");
            }

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);

            // Row-level lock
            await using var selectCmd = new NpgsqlCommand("SELECT status, package_id FROM identity.sellers WHERE id = @id FOR UPDATE", connection, tx);
            selectCmd.Parameters.AddWithValue("id", sellerId);

            string? currentStatus = null;
            Guid? packageId = null;
            await using (var reader = await selectCmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    currentStatus = reader.GetString(0);
                    packageId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                }
            }

            if (currentStatus is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Shop không tồn tại.");
            }

            // Defensive whitelist: strictly allow ACTIVE->SUSPENDED or SUSPENDED->ACTIVE only
            if (!((currentStatus == "ACTIVE" && targetStatus == "SUSPENDED") || (currentStatus == "SUSPENDED" && targetStatus == "ACTIVE")))
            {
                await tx.RollbackAsync(ct);
                if (string.Equals(currentStatus, targetStatus, StringComparison.OrdinalIgnoreCase))
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: $"Shop hiện đã ở trạng thái {targetStatus}.");
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: $"Không thể chuyển trạng thái shop từ {currentStatus} sang {targetStatus}.");
            }

            if (currentStatus == "SUSPENDED" && targetStatus == "ACTIVE")
            {
                if (packageId is null)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Shop chưa đăng ký gói dịch vụ.");
                }

                // Lock referenced package row FOR SHARE to avoid concurrent disable slipping through
                await using var pkgCmd = new NpgsqlCommand("SELECT status FROM identity.subscription_packages WHERE id = @pkgId FOR SHARE", connection, tx);
                pkgCmd.Parameters.AddWithValue("pkgId", packageId.Value);
                var pkgStatus = (string?)await pkgCmd.ExecuteScalarAsync(ct);
                if (pkgStatus != "ACTIVE")
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Gói dịch vụ của shop không còn hoạt động.");
                }

                // Check flow.package_valid
                await using var validCmd = new NpgsqlCommand("SELECT flow.package_valid(@id)", connection, tx);
                validCmd.Parameters.AddWithValue("id", sellerId);
                var isValid = (bool)(await validCmd.ExecuteScalarAsync(ct) ?? false);
                if (!isValid)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Gói dịch vụ của shop đã hết hạn.");
                }
            }

            var now = DateTime.UtcNow;
            await using var updateCmd = new NpgsqlCommand("UPDATE identity.sellers SET status = @status, updated_at = @now WHERE id = @id", connection, tx);
            updateCmd.Parameters.AddWithValue("status", targetStatus);
            updateCmd.Parameters.AddWithValue("now", now);
            updateCmd.Parameters.AddWithValue("id", sellerId);
            try
            {
                await updateCmd.ExecuteNonQueryAsync(ct);

                // Audit in same transaction
                var auditAction = $"SELLER_STATUS_{targetStatus}";
                await using var auditCmd = new NpgsqlCommand("SELECT flow.audit(@actor, @action, 'sellers', @id, jsonb_build_object('from', @from, 'to', @to, 'reason', @reason))", connection, tx);
                auditCmd.Parameters.AddWithValue("actor", managerId);
                auditCmd.Parameters.AddWithValue("action", auditAction);
                auditCmd.Parameters.AddWithValue("id", sellerId);
                auditCmd.Parameters.AddWithValue("from", currentStatus);
                auditCmd.Parameters.AddWithValue("to", targetStatus);
                auditCmd.Parameters.AddWithValue("reason", reason);
                await auditCmd.ExecuteNonQueryAsync(ct);

                // Return snapshot from same transaction to avoid EF re-read race
                await using var selectDetailCmd = new NpgsqlCommand("""
                    SELECT s.id, s.shop_name, s.phone, s.address, s.package_id, p.name,
                           s.package_expires_at, s.package_expires_at_ts, s.contact_email, s.brand_tone,
                           s.created_at
                    FROM identity.sellers s
                    LEFT JOIN identity.subscription_packages p ON p.id = s.package_id
                    WHERE s.id = @id
                    """, connection, tx);
                selectDetailCmd.Parameters.AddWithValue("id", sellerId);

                OperationsSellerDetail detail;
                await using (var detailReader = await selectDetailCmd.ExecuteReaderAsync(ct))
                {
                    await detailReader.ReadAsync(ct);
                    detail = new OperationsSellerDetail(
                        detailReader.GetGuid(0),
                        detailReader.GetString(1),
                        detailReader.GetString(2),
                        detailReader.IsDBNull(3) ? null : detailReader.GetString(3),
                        targetStatus,
                        detailReader.IsDBNull(4) ? null : detailReader.GetGuid(4),
                        detailReader.IsDBNull(5) ? null : detailReader.GetString(5),
                        detailReader.IsDBNull(6) ? null : detailReader.GetFieldValue<DateOnly>(6),
                        detailReader.IsDBNull(7) ? null : detailReader.GetDateTime(7),
                        detailReader.IsDBNull(8) ? null : detailReader.GetString(8),
                        detailReader.IsDBNull(9) ? null : detailReader.GetString(9),
                        detailReader.GetDateTime(10),
                        now);
                }

                await tx.CommitAsync(ct);
                return Results.Ok(detail);
            }
            catch (Exception)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, detail: "Không thể xử lý thay đổi trạng thái lúc này.");
            }
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Chuyển đổi trạng thái shop giữa ACTIVE và SUSPENDED kèm lý do bắt buộc")
        .Produces<OperationsSellerDetail>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // 4. GET /api/operations/sellers/{sellerId}/status-history
        app.MapGet("/api/operations/sellers/{sellerId:guid}/status-history", async (
            Guid sellerId,
            int? page,
            int? pageSize,
            NpgsqlDataSource data,
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";

            var pageNumber = page ?? 1;
            var size = pageSize ?? 20;
            if (pageNumber < 1 || pageNumber > 100000 || size < 1 || size > 100)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Tham số phân trang không hợp lệ.");

            var exists = await db.Sellers.AsNoTracking().AnyAsync(x => x.Id == sellerId, ct);
            if (!exists)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Shop không tồn tại.");

            var history = await SellerStatusHistory.ReadAsync(data, sellerId, pageNumber, size, ct);
            return Results.Ok(history);
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Lịch sử thay đổi trạng thái của shop từ nhật ký kiểm toán")
        .Produces<SellerStatusHistoryResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static OperationsSellerSummary MapToSummary(Seller s) =>
        new(s.Id, s.ShopName, s.Phone, s.Address, s.Status, s.PackageId, s.Package?.Name,
            s.PackageExpiresAt, s.PackageExpiresAtTs, s.ContactEmail, s.BrandTone, s.CreatedAt, s.UpdatedAt);

    private static OperationsSellerDetail MapToDetail(Seller s) =>
        new(s.Id, s.ShopName, s.Phone, s.Address, s.Status, s.PackageId, s.Package?.Name,
            s.PackageExpiresAt, s.PackageExpiresAtTs, s.ContactEmail, s.BrandTone, s.CreatedAt, s.UpdatedAt);
}
