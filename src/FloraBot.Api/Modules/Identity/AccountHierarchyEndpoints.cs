using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record CreateAccountRequest(string Email, string FullName, string Password, string? Phone = null);
public sealed record UpdateAccountStatusRequest(string Status);
public sealed record AccountDirectoryItem(Guid Id, string Email, string FullName, string? Phone, string Status, string Role, DateTime CreatedAt);
public sealed record AccountDetailResponse(Guid Id, string Email, string FullName, string? Phone, string? Address, string Status, string Role, DateTime CreatedAt, DateTime UpdatedAt);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
public sealed record UpdateAccountProfileRequest(string FullName, string? Phone = null, string? Address = null);

public sealed record CustomerPageResponse(List<AccountDetailResponse> Items, int Page, bool HasMore);
public sealed record SellerAccountResponse(Guid Id, Guid SellerId, string Email, string FullName, string? Phone, string? Address, string Status, string Role, DateTime CreatedAt, DateTime UpdatedAt);

public static partial class AccountHierarchyEndpoints
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^(0[35789][0-9]{8}|\+84[35789][0-9]{8})$")]
    internal static partial Regex PhonePattern();

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

        app.MapGet("/api/admin/managers/{id:guid}", async (Guid id, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var manager = await db.Users.AsNoTracking()
                .Include(x => x.Profile)
                .SingleOrDefaultAsync(x => x.Id == id && x.Role == "OPERATIONS_MANAGER", ct);
            if (manager is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Operations Manager không tồn tại.");

            return Results.Ok(ToDetailResponse(manager));
        })
        .RequireAuthorization("Admin")
        .WithTags("Identity")
        .WithSummary("Lấy thông tin chi tiết của Operations Manager")
        .Produces<AccountDetailResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/api/admin/managers/{id:guid}", async (Guid id, UpdateAccountProfileRequest request, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var adminId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var validation = ValidateProfileUpdate(request);
            if (validation is not null) return validation;

            var name = request.FullName.Trim();
            var normalizedPhone = NormalizePhone(request.Phone);
            var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await RecheckActorActiveRoleAsync(db, adminId, "ADMIN", ct))
            {
                await tx.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {id} FOR UPDATE", ct);

            var manager = await db.Users.Include(x => x.Profile).SingleOrDefaultAsync(x => x.Id == id && x.Role == "OPERATIONS_MANAGER", ct);
            if (manager is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Operations Manager không tồn tại.");
            }

            if (normalizedPhone is not null)
            {
                var phoneConflict = await db.Users.AnyAsync(x => x.Phone == normalizedPhone && x.Id != id, ct);
                if (phoneConflict)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
                }
            }

            manager.FullName = name;
            if (request.Phone is not null) manager.Phone = normalizedPhone;
            manager.UpdatedAt = DateTime.UtcNow;

            if (manager.Profile is null)
            {
                manager.Profile = new UserProfile
                {
                    UserId = manager.Id,
                    Address = address,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.UserProfiles.Add(manager.Profile);
            }
            else
            {
                if (request.Address is not null) manager.Profile.Address = address;
                manager.Profile.UpdatedAt = DateTime.UtcNow;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
            }

            var payload = JsonSerializer.Serialize(new { name = manager.FullName, phone = manager.Phone, address = manager.Profile?.Address });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({adminId}, 'MANAGER_UPDATED', 'users', {manager.Id}, {payload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            return Results.Ok(ToDetailResponse(manager));
        })
        .RequireAuthorization("Admin")
        .WithTags("Identity")
        .WithSummary("Cập nhật họ tên, số điện thoại, địa chỉ của Operations Manager")
        .Produces<AccountDetailResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

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

        app.MapGet("/api/operations/technicians/{id:guid}", async (Guid id, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var tech = await db.Users.AsNoTracking()
                .Include(x => x.Profile)
                .SingleOrDefaultAsync(x => x.Id == id && x.Role == "TECHNICIAN", ct);
            if (tech is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Kỹ thuật viên không tồn tại.");

            return Results.Ok(ToDetailResponse(tech));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Lấy thông tin chi tiết của Kỹ thuật viên")
        .Produces<AccountDetailResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/api/operations/technicians/{id:guid}", async (Guid id, UpdateAccountProfileRequest request, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var validation = ValidateProfileUpdate(request);
            if (validation is not null) return validation;

            var name = request.FullName.Trim();
            var normalizedPhone = NormalizePhone(request.Phone);
            var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await RecheckActorActiveRoleAsync(db, callerId, "OPERATIONS_MANAGER", ct))
            {
                await tx.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {id} FOR UPDATE", ct);

            var tech = await db.Users.Include(x => x.Profile).SingleOrDefaultAsync(x => x.Id == id && x.Role == "TECHNICIAN", ct);
            if (tech is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Kỹ thuật viên không tồn tại.");
            }

            if (normalizedPhone is not null)
            {
                var phoneConflict = await db.Users.AnyAsync(x => x.Phone == normalizedPhone && x.Id != id, ct);
                if (phoneConflict)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
                }
            }

            tech.FullName = name;
            if (request.Phone is not null) tech.Phone = normalizedPhone;
            tech.UpdatedAt = DateTime.UtcNow;

            if (tech.Profile is null)
            {
                tech.Profile = new UserProfile
                {
                    UserId = tech.Id,
                    Address = address,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.UserProfiles.Add(tech.Profile);
            }
            else
            {
                if (request.Address is not null) tech.Profile.Address = address;
                tech.Profile.UpdatedAt = DateTime.UtcNow;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
            }

            var payload = JsonSerializer.Serialize(new { name = tech.FullName, phone = tech.Phone, address = tech.Profile?.Address });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({callerId}, 'TECHNICIAN_UPDATED', 'users', {tech.Id}, {payload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            return Results.Ok(ToDetailResponse(tech));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Cập nhật họ tên, số điện thoại, địa chỉ của Kỹ thuật viên")
        .Produces<AccountDetailResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

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

        // ==========================================
        // 4. OPERATIONS MANAGER MANAGING CUSTOMERS
        // ==========================================
        app.MapPost("/api/operations/customers", async (CreateAccountRequest request, FloraDbContext db, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            if (!await RecheckActorActiveRoleAsync(db, callerId, "OPERATIONS_MANAGER", ct))
                return Results.Unauthorized();

            var validation = ValidateAccountInput(request);
            if (validation is not null) return validation;

            var email = request.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(x => x.Email == email, ct))
                return Results.Problem(statusCode: 409, detail: "Email này đã được sử dụng trong hệ thống.");

            var normalizedPhone = NormalizePhone(request.Phone);
            if (normalizedPhone is not null && await db.Users.AnyAsync(x => x.Phone == normalizedPhone, ct))
                return Results.Problem(statusCode: 409, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");

            var customer = new User
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                FullName = request.FullName.Trim(),
                Phone = normalizedPhone,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 12),
                Role = "CUSTOMER",
                Status = "ACTIVE",
                SellerId = null,
                LoyaltyPoints = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Users.Add(customer);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                return Results.Problem(statusCode: 409, detail: "Email hoặc số điện thoại này đã được sử dụng trong hệ thống.");
            }

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'CUSTOMER_CREATED', 'users', @target, jsonb_build_object('email', @email, 'name', @name))");
            audit.Parameters.AddWithValue("actor", callerId);
            audit.Parameters.AddWithValue("target", customer.Id);
            audit.Parameters.AddWithValue("email", customer.Email);
            audit.Parameters.AddWithValue("name", customer.FullName);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Created($"/api/operations/customers/{customer.Id}", ToDetailResponse(customer));
        })
        .RequireAuthorization("OperationsManager")
        .RequireRateLimiting("auth")
        .WithTags("Identity")
        .WithSummary("Tạo tài khoản khách hàng mới bởi Operations Manager")
        .Produces<AccountDetailResponse>(201)
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapGet("/api/operations/customers", async (
            string? search,
            string? name,
            string? email,
            string? status,
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

            var trimmedSearch = search?.Trim();
            var trimmedName = name?.Trim();
            var trimmedEmail = email?.Trim();

            if ((trimmedSearch != null && trimmedSearch.Length > 100) ||
                (trimmedName != null && trimmedName.Length > 100) ||
                (trimmedEmail != null && trimmedEmail.Length > 100))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Từ khóa tìm kiếm tối đa 100 ký tự.");
            }

            var query = db.Users.AsNoTracking().Include(x => x.Profile).Where(x => x.Role == "CUSTOMER").AsQueryable();

            if (!string.IsNullOrWhiteSpace(status))
            {
                var s = status.Trim().ToUpperInvariant();
                if (s is not ("ACTIVE" or "LOCKED" or "DISABLED"))
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Trạng thái không hợp lệ. Chỉ chấp nhận ACTIVE, LOCKED, DISABLED.");
                query = query.Where(x => x.Status == s);
            }

            if (!string.IsNullOrWhiteSpace(trimmedName))
            {
                query = query.Where(x => EF.Functions.ILike(x.FullName, $"%{trimmedName}%"));
            }

            if (!string.IsNullOrWhiteSpace(trimmedEmail))
            {
                query = query.Where(x => x.Email != null && EF.Functions.ILike(x.Email, $"%{trimmedEmail}%"));
            }

            if (!string.IsNullOrWhiteSpace(trimmedSearch))
            {
                query = query.Where(x => EF.Functions.ILike(x.FullName, $"%{trimmedSearch}%") ||
                                         (x.Email != null && EF.Functions.ILike(x.Email, $"%{trimmedSearch}%")) ||
                                         (x.Phone != null && EF.Functions.ILike(x.Phone, $"%{trimmedSearch}%")));
            }

            query = query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);
            var rows = await query.Skip((pageNumber - 1) * size).Take(size + 1).ToListAsync(ct);
            var items = rows.Take(size).Select(ToDetailResponse).ToList();

            return Results.Ok(new CustomerPageResponse(items, pageNumber, rows.Count > size));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Danh sách khách hàng cho Operations Manager quản lý, có phân trang và lọc")
        .Produces<CustomerPageResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden);

        app.MapGet("/api/operations/customers/{id:guid}", async (Guid id, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var customer = await db.Users.AsNoTracking()
                .Include(x => x.Profile)
                .SingleOrDefaultAsync(x => x.Id == id && x.Role == "CUSTOMER", ct);

            if (customer is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Khách hàng không tồn tại.");

            return Results.Ok(ToDetailResponse(customer));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Xem thông tin chi tiết khách hàng")
        .Produces<AccountDetailResponse>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/api/operations/customers/{id:guid}", async (Guid id, UpdateAccountProfileRequest request, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var validation = ValidateProfileUpdate(request);
            if (validation is not null) return validation;

            var name = request.FullName.Trim();
            var normalizedPhone = NormalizePhone(request.Phone);
            var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await RecheckActorActiveRoleAsync(db, callerId, "OPERATIONS_MANAGER", ct))
            {
                await tx.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {id} FOR UPDATE", ct);

            var customer = await db.Users.Include(x => x.Profile).SingleOrDefaultAsync(x => x.Id == id && x.Role == "CUSTOMER", ct);
            if (customer is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Khách hàng không tồn tại.");
            }

            if (normalizedPhone is not null)
            {
                var phoneConflict = await db.Users.AnyAsync(x => x.Phone == normalizedPhone && x.Id != id, ct);
                if (phoneConflict)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
                }
            }

            customer.FullName = name;
            if (request.Phone is not null) customer.Phone = normalizedPhone;
            customer.UpdatedAt = DateTime.UtcNow;

            if (customer.Profile is null)
            {
                customer.Profile = new UserProfile
                {
                    UserId = customer.Id,
                    Address = address,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.UserProfiles.Add(customer.Profile);
            }
            else
            {
                if (request.Address is not null) customer.Profile.Address = address;
                customer.Profile.UpdatedAt = DateTime.UtcNow;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
            }

            var payload = JsonSerializer.Serialize(new { name = customer.FullName, phone = customer.Phone, address = customer.Profile?.Address });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({callerId}, 'CUSTOMER_UPDATED', 'users', {customer.Id}, {payload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            return Results.Ok(ToDetailResponse(customer));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Cập nhật họ tên, số điện thoại, địa chỉ của khách hàng")
        .Produces<AccountDetailResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPatch("/api/operations/customers/{id:guid}/status", async (Guid id, UpdateAccountStatusRequest request, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var status = request?.Status?.Trim().ToUpperInvariant();
            if (status is not ("ACTIVE" or "LOCKED" or "DISABLED"))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Trạng thái hợp lệ: ACTIVE, LOCKED, DISABLED");

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await RecheckActorActiveRoleAsync(db, callerId, "OPERATIONS_MANAGER", ct))
            {
                await tx.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {id} FOR UPDATE", ct);

            var customer = await db.Users.Include(x => x.Profile).SingleOrDefaultAsync(x => x.Id == id && x.Role == "CUSTOMER", ct);
            if (customer is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Khách hàng không tồn tại.");
            }

            if (customer.Status == status)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: $"Khách hàng hiện đã ở trạng thái {status}.");
            }

            customer.Status = status;
            customer.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var payload = JsonSerializer.Serialize(new { status });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({callerId}, 'CUSTOMER_STATUS_UPDATED', 'users', {customer.Id}, {payload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            return Results.Ok(ToDetailResponse(customer));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Cập nhật trạng thái khách hàng (ACTIVE, LOCKED, DISABLED)")
        .Produces<AccountDetailResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

        // ==========================================
        // 5. OPERATIONS MANAGER MANAGING SELLER ACCOUNTS
        // ==========================================
        app.MapGet("/api/operations/sellers/{sellerId:guid}/accounts", async (Guid sellerId, FloraDbContext db, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var sellerExists = await db.Sellers.AsNoTracking().AnyAsync(x => x.Id == sellerId, ct);
            if (!sellerExists)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Shop không tồn tại.");

            var accounts = await db.Users.AsNoTracking()
                .Include(u => u.Profile)
                .Where(x => x.Role == "SELLER" && x.SellerId == sellerId)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => ToSellerAccountResponse(x, sellerId))
                .ToListAsync(ct);

            return Results.Ok(accounts);
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Xem danh sách tài khoản chủ shop (SELLER) thuộc shop được chỉ định")
        .Produces<List<SellerAccountResponse>>()
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPut("/api/operations/sellers/{sellerId:guid}/accounts/{userId:guid}", async (
            Guid sellerId,
            Guid userId,
            UpdateAccountProfileRequest request,
            FloraDbContext db,
            HttpContext http,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var callerId = Guid.Parse(http.User.FindFirstValue("sub")!);
            var validation = ValidateProfileUpdate(request);
            if (validation is not null) return validation;

            var name = request.FullName.Trim();
            var normalizedPhone = NormalizePhone(request.Phone);
            var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim();

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await RecheckActorActiveRoleAsync(db, callerId, "OPERATIONS_MANAGER", ct))
            {
                await tx.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {userId} FOR UPDATE", ct);

            // Strictly ensure role is SELLER and sellerId matches! Cannot modify ADMIN, MANAGER, SELLER_STAFF, or cross-shop!
            var user = await db.Users.Include(u => u.Profile)
                .SingleOrDefaultAsync(x => x.Id == userId && x.Role == "SELLER" && x.SellerId == sellerId, ct);

            if (user is null)
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: "Tài khoản chủ shop không tồn tại hoặc không khớp.");
            }

            if (normalizedPhone is not null)
            {
                var phoneConflict = await db.Users.AnyAsync(x => x.Phone == normalizedPhone && x.Id != userId, ct);
                if (phoneConflict)
                {
                    await tx.RollbackAsync(ct);
                    return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
                }
            }

            user.FullName = name;
            if (request.Phone is not null) user.Phone = normalizedPhone;
            user.UpdatedAt = DateTime.UtcNow;

            if (user.Profile is null)
            {
                user.Profile = new UserProfile
                {
                    UserId = user.Id,
                    Address = address,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.UserProfiles.Add(user.Profile);
            }
            else
            {
                if (request.Address is not null) user.Profile.Address = address;
                user.Profile.UpdatedAt = DateTime.UtcNow;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                await tx.RollbackAsync(ct);
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, detail: "Số điện thoại này đã được sử dụng trong hệ thống.");
            }

            var payload = JsonSerializer.Serialize(new { seller_id = sellerId, name = user.FullName, phone = user.Phone, address = user.Profile?.Address });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({callerId}, 'SELLER_ACCOUNT_UPDATED', 'users', {user.Id}, {payload}::jsonb)", ct);

            await tx.CommitAsync(ct);

            return Results.Ok(ToSellerAccountResponse(user, sellerId));
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("Identity")
        .WithSummary("Cập nhật họ tên, số điện thoại, địa chỉ của tài khoản chủ shop (SELLER)")
        .Produces<SellerAccountResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);
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

    internal static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var trimmed = phone.Trim();
        return trimmed.StartsWith("+84", StringComparison.Ordinal) ? "0" + trimmed[3..] : trimmed;
    }

    internal static IResult? ValidateProfileUpdate(UpdateAccountProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length is < 2 or > 120 || request.FullName.Any(char.IsControl))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Họ và tên phải từ 2 đến 120 ký tự và không chứa ký tự điều khiển.");

        if (!string.IsNullOrWhiteSpace(request.Phone) && !PhonePattern().IsMatch(request.Phone.Trim()))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Số điện thoại Việt Nam không hợp lệ.");

        if (request.Address is not null)
        {
            var address = request.Address.Trim();
            if (address.Length > 300 || address.Any(char.IsControl))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Địa chỉ tối đa 300 ký tự và không chứa ký tự điều khiển.");
        }

        return null;
    }

    internal static AccountDetailResponse ToDetailResponse(User u) =>
        new(u.Id, u.Email ?? "", u.FullName, u.Phone, u.Profile?.Address, u.Status, u.Role, u.CreatedAt, u.UpdatedAt);

    internal static SellerAccountResponse ToSellerAccountResponse(User u, Guid sellerId) =>
        new(u.Id, sellerId, u.Email ?? "", u.FullName, u.Phone, u.Profile?.Address, u.Status, u.Role, u.CreatedAt, u.UpdatedAt);

    internal static async Task<bool> RecheckActorActiveRoleAsync(FloraDbContext db, Guid actorId, string expectedRole, CancellationToken ct)
    {
        var user = await db.Users.FromSqlInterpolated(
            $"SELECT * FROM identity.users WHERE id = {actorId} AND role = {expectedRole} AND status = 'ACTIVE' FOR SHARE")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        return user is not null;
    }

    private static AccountDirectoryItem ToDirectoryItem(User u) =>
        new(u.Id, u.Email ?? "", u.FullName, u.Phone, u.Status, u.Role, u.CreatedAt);
}
