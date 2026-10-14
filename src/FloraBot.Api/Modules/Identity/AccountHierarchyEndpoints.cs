using System.Security.Claims;
using System.Text.RegularExpressions;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record CreateAccountRequest(string Email, string FullName, string Password, string? Phone = null);
public sealed record UpdateAccountStatusRequest(string Status);
public sealed record AccountDirectoryItem(Guid Id, string Email, string FullName, string? Phone, string Status, string Role, DateTime CreatedAt);

public static partial class AccountHierarchyEndpoints
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^(0[35789][0-9]{8}|\+84[35789][0-9]{8})$")]
    private static partial Regex PhonePattern();

    public static void MapAccountHierarchy(this WebApplication app)
    {
        // ==========================================
        // 1. ADMIN MANAGING OPERATIONS MANAGERS
        // ==========================================
        app.MapPost("/api/admin/managers", async (CreateAccountRequest request, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            var adminId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var validation = ValidateAccountInput(request);
            if (validation is not null) return validation;

            var email = request.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(x => x.Email == email, ct))
                return Results.Problem(statusCode: 409, detail: "Email này đã được sử dụng trong hệ thống.");

            var manager = new User
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                FullName = request.FullName.Trim(),
                Phone = request.Phone?.Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12),
                Role = "OPERATIONS_MANAGER",
                Status = "ACTIVE",
                SellerId = null,
                LoyaltyPoints = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Users.Add(manager);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                return Results.Problem(statusCode: 409, detail: "Email hoặc số điện thoại này đã được sử dụng trong hệ thống.");
            }

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'MANAGER_CREATED', 'users', @target, jsonb_build_object('email', @email, 'name', @name))");
            audit.Parameters.AddWithValue("actor", adminId);
            audit.Parameters.AddWithValue("target", manager.Id);
            audit.Parameters.AddWithValue("email", manager.Email);
            audit.Parameters.AddWithValue("name", manager.FullName);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Created($"/api/admin/managers/{manager.Id}", ToDirectoryItem(manager));
        }).RequireAuthorization("Admin").RequireRateLimiting("auth").WithTags("Identity").Produces<AccountDirectoryItem>(201);

        app.MapGet("/api/admin/managers", async (FloraDbContext db, CancellationToken ct) =>
        {
            var managers = await db.Users.AsNoTracking()
                .Where(x => x.Role == "OPERATIONS_MANAGER")
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new AccountDirectoryItem(x.Id, x.Email ?? "", x.FullName, x.Phone, x.Status, x.Role, x.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(managers);
        }).RequireAuthorization("Admin").WithTags("Identity").Produces<List<AccountDirectoryItem>>();

        app.MapPatch("/api/admin/managers/{id:guid}/status", async (Guid id, UpdateAccountStatusRequest request, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            var adminId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var status = request.Status?.Trim().ToUpperInvariant();
            if (status is not ("ACTIVE" or "LOCKED" or "DISABLED"))
                return Results.BadRequest(new { error = "Trạng thái hợp lệ: ACTIVE, LOCKED, DISABLED" });

            var manager = await db.Users.SingleOrDefaultAsync(x => x.Id == id && x.Role == "OPERATIONS_MANAGER", ct);
            if (manager is null) return Results.NotFound();

            manager.Status = status;
            manager.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'MANAGER_STATUS_UPDATED', 'users', @target, jsonb_build_object('status', @status))");
            audit.Parameters.AddWithValue("actor", adminId);
            audit.Parameters.AddWithValue("target", manager.Id);
            audit.Parameters.AddWithValue("status", status);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Ok(ToDirectoryItem(manager));
        }).RequireAuthorization("Admin").WithTags("Identity").Produces<AccountDirectoryItem>();

        // ==========================================
        // 2. OPERATIONS MANAGER MANAGING TECHNICIANS
        // ==========================================
        app.MapPost("/api/operations/technicians", async (CreateAccountRequest request, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var validation = ValidateAccountInput(request);
            if (validation is not null) return validation;

            var email = request.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(x => x.Email == email, ct))
                return Results.Problem(statusCode: 409, detail: "Email này đã được sử dụng trong hệ thống.");

            var tech = new User
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                FullName = request.FullName.Trim(),
                Phone = request.Phone?.Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12),
                Role = "TECHNICIAN",
                Status = "ACTIVE",
                SellerId = null,
                LoyaltyPoints = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Users.Add(tech);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                return Results.Problem(statusCode: 409, detail: "Email hoặc số điện thoại này đã được sử dụng trong hệ thống.");
            }

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'TECHNICIAN_CREATED', 'users', @target, jsonb_build_object('email', @email, 'name', @name))");
            audit.Parameters.AddWithValue("actor", callerId);
            audit.Parameters.AddWithValue("target", tech.Id);
            audit.Parameters.AddWithValue("email", tech.Email);
            audit.Parameters.AddWithValue("name", tech.FullName);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Created($"/api/operations/technicians/{tech.Id}", ToDirectoryItem(tech));
        }).RequireAuthorization("OperationsManager").RequireRateLimiting("auth").WithTags("Identity").Produces<AccountDirectoryItem>(201);

        app.MapGet("/api/operations/technicians", async (FloraDbContext db, CancellationToken ct) =>
        {
            var technicians = await db.Users.AsNoTracking()
                .Where(x => x.Role == "TECHNICIAN")
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new AccountDirectoryItem(x.Id, x.Email ?? "", x.FullName, x.Phone, x.Status, x.Role, x.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(technicians);
        }).RequireAuthorization("OperationsManager").WithTags("Identity").Produces<List<AccountDirectoryItem>>();

        app.MapPatch("/api/operations/technicians/{id:guid}/status", async (Guid id, UpdateAccountStatusRequest request, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var status = request.Status?.Trim().ToUpperInvariant();
            if (status is not ("ACTIVE" or "LOCKED" or "DISABLED"))
                return Results.BadRequest(new { error = "Trạng thái hợp lệ: ACTIVE, LOCKED, DISABLED" });

            var tech = await db.Users.SingleOrDefaultAsync(x => x.Id == id && x.Role == "TECHNICIAN", ct);
            if (tech is null) return Results.NotFound();

            tech.Status = status;
            tech.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'TECHNICIAN_STATUS_UPDATED', 'users', @target, jsonb_build_object('status', @status))");
            audit.Parameters.AddWithValue("actor", callerId);
            audit.Parameters.AddWithValue("target", tech.Id);
            audit.Parameters.AddWithValue("status", status);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Ok(ToDirectoryItem(tech));
        }).RequireAuthorization("OperationsManager").WithTags("Identity").Produces<AccountDirectoryItem>();

        // ==========================================
        // 3. SELLER MANAGING SELLER STAFF
        // ==========================================
        app.MapPost("/api/sellers/{sellerId:guid}/staff", async (Guid sellerId, CreateAccountRequest request, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var validation = ValidateAccountInput(request);
            if (validation is not null) return validation;

            var seller = await db.Sellers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sellerId, ct);
            if (seller is null) return Results.NotFound(new { error = "Shop không tồn tại." });

            var email = request.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(x => x.Email == email, ct))
                return Results.Problem(statusCode: 409, detail: "Email này đã được sử dụng trong hệ thống.");

            var staff = new User
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                FullName = request.FullName.Trim(),
                Phone = request.Phone?.Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12),
                Role = "SELLER_STAFF",
                Status = "ACTIVE",
                SellerId = sellerId,
                LoyaltyPoints = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Users.Add(staff);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                return Results.Problem(statusCode: 409, detail: "Email hoặc số điện thoại này đã được sử dụng trong hệ thống.");
            }

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'SELLER_STAFF_CREATED', 'users', @target, jsonb_build_object('email', @email, 'name', @name, 'seller_id', @seller))");
            audit.Parameters.AddWithValue("actor", callerId);
            audit.Parameters.AddWithValue("target", staff.Id);
            audit.Parameters.AddWithValue("email", staff.Email);
            audit.Parameters.AddWithValue("name", staff.FullName);
            audit.Parameters.AddWithValue("seller", sellerId);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Created($"/api/sellers/{sellerId}/staff/{staff.Id}", ToDirectoryItem(staff));
        }).RequireAuthorization("Seller", "SameSeller").RequireRateLimiting("auth").WithTags("Identity").Produces<AccountDirectoryItem>(201);

        app.MapGet("/api/sellers/{sellerId:guid}/staff/accounts", async (Guid sellerId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var staff = await db.Users.AsNoTracking()
                .Where(x => x.Role == "SELLER_STAFF" && x.SellerId == sellerId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => new AccountDirectoryItem(x.Id, x.Email ?? "", x.FullName, x.Phone, x.Status, x.Role, x.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(staff);
        }).RequireAuthorization("Seller", "SameSeller").WithTags("Identity").Produces<List<AccountDirectoryItem>>();

        app.MapPatch("/api/sellers/{sellerId:guid}/staff/{staffId:guid}/status", async (Guid sellerId, Guid staffId, UpdateAccountStatusRequest request, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var status = request.Status?.Trim().ToUpperInvariant();
            if (status is not ("ACTIVE" or "LOCKED" or "DISABLED"))
                return Results.BadRequest(new { error = "Trạng thái hợp lệ: ACTIVE, LOCKED, DISABLED" });

            var staff = await db.Users.SingleOrDefaultAsync(x => x.Id == staffId && x.Role == "SELLER_STAFF" && x.SellerId == sellerId, ct);
            if (staff is null) return Results.NotFound();

            staff.Status = status;
            staff.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'SELLER_STAFF_STATUS_UPDATED', 'users', @target, jsonb_build_object('status', @status, 'seller_id', @seller))");
            audit.Parameters.AddWithValue("actor", callerId);
            audit.Parameters.AddWithValue("target", staff.Id);
            audit.Parameters.AddWithValue("status", status);
            audit.Parameters.AddWithValue("seller", sellerId);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Ok(ToDirectoryItem(staff));
        }).RequireAuthorization("Seller", "SameSeller").WithTags("Identity").Produces<AccountDirectoryItem>();
    }

    private static IResult? ValidateAccountInput(CreateAccountRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Trim().Length > 254 || !EmailPattern().IsMatch(request.Email.Trim()))
            return Results.BadRequest(new { error = "Email không đúng định dạng." });
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length is < 2 or > 120)
            return Results.BadRequest(new { error = "Họ và tên phải từ 2 đến 120 ký tự." });
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6 || System.Text.Encoding.UTF8.GetByteCount(request.Password) > 72)
            return Results.BadRequest(new { error = "Mật khẩu phải từ 6 đến 72 ký tự." });
        if (!string.IsNullOrWhiteSpace(request.Phone) && !PhonePattern().IsMatch(request.Phone.Trim()))
            return Results.BadRequest(new { error = "Số điện thoại Việt Nam không hợp lệ." });
        return null;
    }

    private static AccountDirectoryItem ToDirectoryItem(User u) =>
        new(u.Id, u.Email ?? "", u.FullName, u.Phone, u.Status, u.Role, u.CreatedAt);
}
