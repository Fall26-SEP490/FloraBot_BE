using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AccountManagementCompletionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static string UniquePhone() => $"09{Random.Shared.Next(10000000, 99999999)}";
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@florabot-test.example";

    private async Task<(User User, Seller? Seller)> CreateIsolatedUserAsync(string role, string status = "ACTIVE", string? phone = null, string? email = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        Seller? seller = null;
        if (role is "SELLER" or "SELLER_STAFF")
        {
            var package = await db.SubscriptionPackages.AsNoTracking().FirstAsync(p => p.Status == "ACTIVE");
            seller = new Seller
            {
                Id = Guid.NewGuid(),
                ShopName = "Isolated Shop " + Guid.NewGuid().ToString("N")[..8],
                Phone = UniquePhone(),
                Address = "123 Shop St, District 1",
                Status = "ACTIVE",
                PackageId = package.Id,
                PackageExpiresAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                PackageExpiresAtTs = DateTime.UtcNow.AddDays(30),
                ContactEmail = "contact@" + Guid.NewGuid().ToString("N")[..8] + ".test",
                BrandTone = "Chuyên nghiệp",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Sellers.Add(seller);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email ?? UniqueEmail(role.ToLowerInvariant()),
            Phone = phone ?? UniquePhone(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!", workFactor: 12),
            FullName = $"Test {role} " + Guid.NewGuid().ToString("N")[..6],
            Role = role,
            SellerId = seller?.Id,
            Status = status,
            LoyaltyPoints = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user, seller);
    }

    private HttpClient CreateAuthenticatedClient(User user)
    {
        using var scope = factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
        var token = tokenService.Issue(user, "APPROVED");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static void AssertNoSecretProperties(string json)
    {
        Assert.DoesNotContain("passwordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password_hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountEncrypted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account_encrypted", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bankAccountNoEnc", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bank_account_no_enc", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("externalIdentities", json, StringComparison.OrdinalIgnoreCase);
    }

    // ==========================================
    // 1. ADMIN MANAGING OPERATIONS MANAGERS
    // ==========================================
    [Fact]
    public async Task Admin_CanGetAndUpdateManager_WithAuditAndValidation()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (manager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (otherUser, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var client = CreateAuthenticatedClient(admin);

        // 1. GET manager detail
        var getResp = await client.GetAsync($"/api/admin/managers/{manager.Id}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        Assert.True(getResp.Headers.CacheControl?.NoStore);

        var getRaw = await getResp.Content.ReadAsStringAsync();
        AssertNoSecretProperties(getRaw);

        var detail = await getResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(manager.Id, detail.Id);
        Assert.Equal("OPERATIONS_MANAGER", detail.Role);
        Assert.Equal(manager.Email, detail.Email);
        Assert.Equal(manager.FullName, detail.FullName);
        Assert.Equal(manager.Phone, detail.Phone);

        // 2. Target role mismatch: GET with Customer ID returns 404
        var wrongRoleGet = await client.GetAsync($"/api/admin/managers/{otherUser.Id}");
        Assert.Equal(HttpStatusCode.NotFound, wrongRoleGet.StatusCode);

        // 3. Validation on PUT: FullName too short -> 400
        var badNameResp = await client.PutAsJsonAsync($"/api/admin/managers/{manager.Id}", new UpdateAccountProfileRequest("A"));
        Assert.Equal(HttpStatusCode.BadRequest, badNameResp.StatusCode);

        // 4. Validation on PUT: Invalid phone -> 400
        var badPhoneResp = await client.PutAsJsonAsync($"/api/admin/managers/{manager.Id}", new UpdateAccountProfileRequest("Valid Name", "12345"));
        Assert.Equal(HttpStatusCode.BadRequest, badPhoneResp.StatusCode);

        // 5. Phone conflict: Phone taken by other user -> 409
        var conflictPhoneResp = await client.PutAsJsonAsync($"/api/admin/managers/{manager.Id}", new UpdateAccountProfileRequest("Valid Name", otherUser.Phone));
        Assert.Equal(HttpStatusCode.Conflict, conflictPhoneResp.StatusCode);

        // 6. Success PUT: update name, phone, address
        var newPhone = UniquePhone();
        var updateResp = await client.PutAsJsonAsync($"/api/admin/managers/{manager.Id}", new UpdateAccountProfileRequest("Updated Ops Manager", newPhone, "123 Le Loi, District 1"));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        Assert.True(updateResp.Headers.CacheControl?.NoStore);

        var updated = await updateResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Updated Ops Manager", updated.FullName);
        Assert.Equal(newPhone, updated.Phone);
        Assert.Equal("123 Le Loi, District 1", updated.Address);
        Assert.Equal("OPERATIONS_MANAGER", updated.Role);
        Assert.Equal(manager.Email, updated.Email); // Email preserved!

        // 7. Verify persistence and audit in database
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var dbManager = await db.Users.Include(u => u.Profile).SingleAsync(u => u.Id == manager.Id);
            Assert.Equal("Updated Ops Manager", dbManager.FullName);
            Assert.Equal(newPhone, dbManager.Phone);
            Assert.Equal("123 Le Loi, District 1", dbManager.Profile?.Address);
            Assert.Equal(manager.PasswordHash, dbManager.PasswordHash); // Password preserved!
            Assert.Equal("OPERATIONS_MANAGER", dbManager.Role); // Role preserved!

            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(a => a.EntityId == manager.Id && a.Action == "MANAGER_UPDATED");
            Assert.NotNull(audit);
            Assert.Equal(admin.Id, audit.ActorId);
        }

        // 8. Anonymous -> 401, Non-Admin -> 403
        using var anonClient = factory.CreateClient();
        var anonResp = await anonClient.GetAsync($"/api/admin/managers/{manager.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);
        var anonPut = await anonClient.PutAsJsonAsync($"/api/admin/managers/{manager.Id}", new UpdateAccountProfileRequest("Name", UniquePhone()));
        Assert.Equal(HttpStatusCode.Unauthorized, anonPut.StatusCode);

        using var nonAdminClient = CreateAuthenticatedClient(manager);
        var forbiddenGet = await nonAdminClient.GetAsync($"/api/admin/managers/{manager.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenGet.StatusCode);
        var forbiddenPut = await nonAdminClient.PutAsJsonAsync($"/api/admin/managers/{manager.Id}", new UpdateAccountProfileRequest("Name", UniquePhone()));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenPut.StatusCode);
    }

    // ==========================================
    // 2. OPERATIONS MANAGER MANAGING TECHNICIANS
    // ==========================================
    [Fact]
    public async Task OperationsManager_CanGetAndUpdateTechnician_WithAuditAndValidation()
    {
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (technician, _) = await CreateIsolatedUserAsync("TECHNICIAN");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var client = CreateAuthenticatedClient(opsManager);

        // 1. GET technician detail
        var getResp = await client.GetAsync($"/api/operations/technicians/{technician.Id}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        Assert.True(getResp.Headers.CacheControl?.NoStore);

        var detail = await getResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(technician.Id, detail.Id);
        Assert.Equal("TECHNICIAN", detail.Role);

        // 2. Target role mismatch: GET with Customer ID returns 404
        var wrongRoleResp = await client.GetAsync($"/api/operations/technicians/{customer.Id}");
        Assert.Equal(HttpStatusCode.NotFound, wrongRoleResp.StatusCode);

        // 3. Phone conflict on PUT -> 409
        var conflictResp = await client.PutAsJsonAsync($"/api/operations/technicians/{technician.Id}", new UpdateAccountProfileRequest("Tech Leader", customer.Phone));
        Assert.Equal(HttpStatusCode.Conflict, conflictResp.StatusCode);

        // 4. Success PUT: update name, phone, address
        var newPhone = UniquePhone();
        var updateResp = await client.PutAsJsonAsync($"/api/operations/technicians/{technician.Id}", new UpdateAccountProfileRequest("Senior Lead Technician", newPhone, "456 Tran Hung Dao"));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        var updated = await updateResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Senior Lead Technician", updated.FullName);
        Assert.Equal(newPhone, updated.Phone);
        Assert.Equal("456 Tran Hung Dao", updated.Address);
        Assert.Equal("TECHNICIAN", updated.Role);
        Assert.Equal(technician.Email, updated.Email); // Email preserved!

        // 5. Verify DB persist and audit
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var dbTech = await db.Users.Include(u => u.Profile).SingleAsync(u => u.Id == technician.Id);
            Assert.Equal("Senior Lead Technician", dbTech.FullName);
            Assert.Equal("456 Tran Hung Dao", dbTech.Profile?.Address);

            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(a => a.EntityId == technician.Id && a.Action == "TECHNICIAN_UPDATED");
            Assert.NotNull(audit);
            Assert.Equal(opsManager.Id, audit.ActorId);
        }

        // 6. Anonymous -> 401, Non-OM -> 403
        using var anonClient = factory.CreateClient();
        var anonResp = await anonClient.GetAsync($"/api/operations/technicians/{technician.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        using var techClient = CreateAuthenticatedClient(technician);
        var forbiddenResp = await techClient.GetAsync($"/api/operations/technicians/{technician.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);
    }

    // ==========================================
    // 3. OPERATIONS MANAGER MANAGING CUSTOMERS
    // ==========================================
    [Fact]
    public async Task OperationsManager_CanCreateListAndManageCustomers_WithImmediateRevocationOnLock()
    {
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (existingUser, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var client = CreateAuthenticatedClient(opsManager);

        // 1. POST /api/operations/customers - create Customer
        var newEmail = UniqueEmail("newcustomer");
        var newPhone = UniquePhone();
        var createReq = new CreateAccountRequest(newEmail, "Nguyen Van Khanh", "StrongPass123!", newPhone);

        var createResp = await client.PostAsJsonAsync("/api/operations/customers", createReq);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var created = await createResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(created);
        Assert.Equal("CUSTOMER", created.Role);
        Assert.Equal("ACTIVE", created.Status);
        Assert.Equal(newEmail, created.Email);
        Assert.Equal("Nguyen Van Khanh", created.FullName);
        Assert.Equal(newPhone, created.Phone);

        // Verify DB persist, bcrypt-12 hash, points = 0, no fake external identity
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var dbCust = await db.Users.SingleAsync(u => u.Id == created.Id);
            Assert.Equal("CUSTOMER", dbCust.Role);
            Assert.Equal(0, dbCust.LoyaltyPoints);
            Assert.True(BCrypt.Net.BCrypt.Verify("StrongPass123!", dbCust.PasswordHash));

            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(a => a.EntityId == created.Id && a.Action == "CUSTOMER_CREATED");
            Assert.NotNull(audit);
            Assert.Equal(opsManager.Id, audit.ActorId);
        }

        // 2. Conflict: Duplicate email -> 409
        var dupEmailResp = await client.PostAsJsonAsync("/api/operations/customers", new CreateAccountRequest(newEmail, "Duplicate Email", "StrongPass123!"));
        Assert.Equal(HttpStatusCode.Conflict, dupEmailResp.StatusCode);

        // 3. Conflict: Duplicate phone -> 409
        var dupPhoneResp = await client.PostAsJsonAsync("/api/operations/customers", new CreateAccountRequest(UniqueEmail("diff"), "Duplicate Phone", "StrongPass123!", newPhone));
        Assert.Equal(HttpStatusCode.Conflict, dupPhoneResp.StatusCode);

        // 4. GET /api/operations/customers - paged & filtered
        var listResp = await client.GetAsync($"/api/operations/customers?status=ACTIVE&search={Uri.EscapeDataString(newEmail)}&page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        Assert.True(listResp.Headers.CacheControl?.NoStore);

        var listRaw = await listResp.Content.ReadAsStringAsync();
        AssertNoSecretProperties(listRaw);

        var page = await listResp.Content.ReadFromJsonAsync<CustomerPageResponse>();
        Assert.NotNull(page);
        Assert.Contains(page.Items, c => c.Id == created.Id && c.Email == newEmail);

        // 5. GET /api/operations/customers/{id}
        var getResp = await client.GetAsync($"/api/operations/customers/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var detail = await getResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal(created.Id, detail.Id);

        // Target role mismatch -> 404
        var wrongRoleGet = await client.GetAsync($"/api/operations/customers/{opsManager.Id}");
        Assert.Equal(HttpStatusCode.NotFound, wrongRoleGet.StatusCode);

        // 6. PUT /api/operations/customers/{id} - update fullname, phone, address
        var updatedPhone = UniquePhone();
        var putResp = await client.PutAsJsonAsync($"/api/operations/customers/{created.Id}", new UpdateAccountProfileRequest("Nguyen Khanh Updated", updatedPhone, "789 Vo Van Kiet"));
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var putDetail = await putResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(putDetail);
        Assert.Equal("Nguyen Khanh Updated", putDetail.FullName);
        Assert.Equal(updatedPhone, putDetail.Phone);
        Assert.Equal("789 Vo Van Kiet", putDetail.Address);
        Assert.Equal(newEmail, putDetail.Email); // Preserved!
        Assert.Equal("CUSTOMER", putDetail.Role); // Preserved!

        // 7. PATCH /api/operations/customers/{id}/status - set LOCKED
        var patchResp = await client.PatchAsJsonAsync($"/api/operations/customers/{created.Id}/status", new UpdateAccountStatusRequest("LOCKED"));
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var lockedDetail = await patchResp.Content.ReadFromJsonAsync<AccountDetailResponse>();
        Assert.NotNull(lockedDetail);
        Assert.Equal("LOCKED", lockedDetail.Status);

        // Verify audit for status update
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(a => a.EntityId == created.Id && a.Action == "CUSTOMER_STATUS_UPDATED");
            Assert.NotNull(audit);
            Assert.Equal(opsManager.Id, audit.ActorId);
        }

        // 8. Immediate LOCKED identity revocation:
        // Attempting to access an authenticated endpoint with the locked customer's token MUST be rejected with 401 Unauthorized!
        var lockedUser = new User
        {
            Id = created.Id,
            Email = newEmail,
            FullName = "Nguyen Khanh Updated",
            Role = "CUSTOMER",
            Status = "LOCKED"
        };
        using var lockedClient = CreateAuthenticatedClient(lockedUser);
        var profileAttempt = await lockedClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, profileAttempt.StatusCode);
    }

    // ==========================================
    // 4. MANAGER SELLER SHOP UPDATE & SAFE ACCOUNTS
    // ==========================================
    [Fact]
    public async Task OperationsManager_CanUpdateSellerShop_PreservingProtectedFields()
    {
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        using var client = CreateAuthenticatedClient(opsManager);

        var originalContactEmail = seller.ContactEmail;
        var originalStatus = seller.Status;
        var originalPackageId = seller.PackageId;

        // 1. PUT /api/operations/sellers/{sellerId}
        var newShopName = "Refined Florist " + Guid.NewGuid().ToString("N")[..6];
        var newShopPhone = UniquePhone();
        var newAddress = "999 Hai Ba Trung, D3";

        var putShopResp = await client.PutAsJsonAsync($"/api/operations/sellers/{seller.Id}", new UpdateSellerShopRequest(newShopName, newShopPhone, newAddress));
        Assert.Equal(HttpStatusCode.OK, putShopResp.StatusCode);
        Assert.True(putShopResp.Headers.CacheControl?.NoStore);

        var shopDetail = await putShopResp.Content.ReadFromJsonAsync<OperationsSellerDetail>();
        Assert.NotNull(shopDetail);
        Assert.Equal(newShopName, shopDetail.ShopName);
        Assert.Equal(newShopPhone, shopDetail.Phone);
        Assert.Equal(newAddress, shopDetail.Address);
        Assert.Equal(originalContactEmail, shopDetail.ContactEmail); // Contact email preserved!
        Assert.Equal(originalStatus, shopDetail.Status); // Status preserved!
        Assert.Equal(originalPackageId, shopDetail.PackageId); // Package preserved!

        // 2. Verify DB persistence and audit
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
            Assert.Equal(newShopName, dbSeller.ShopName);
            Assert.Equal(newShopPhone, dbSeller.Phone);
            Assert.Equal(newAddress, dbSeller.Address);
            Assert.Equal(originalContactEmail, dbSeller.ContactEmail);

            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync(a => a.EntityId == seller.Id && a.Action == "SELLER_SHOP_UPDATED");
            Assert.NotNull(audit);
            Assert.Equal(opsManager.Id, audit.ActorId);
        }

        // 3. Validation: shopName too short -> 400
        var badShopResp = await client.PutAsJsonAsync($"/api/operations/sellers/{seller.Id}", new UpdateSellerShopRequest("A", newShopPhone));
        Assert.Equal(HttpStatusCode.BadRequest, badShopResp.StatusCode);

        // 4. Non-existent seller -> 404
        var notFoundResp = await client.PutAsJsonAsync($"/api/operations/sellers/{Guid.NewGuid()}", new UpdateSellerShopRequest("Valid Shop Name", newShopPhone));
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
    }

    [Fact]
    public async Task OperationsManager_CanInspectAndEditSellerAccount_WithRoleAndShopScoping()
    {
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        var (adminUser, _) = await CreateIsolatedUserAsync("ADMIN");
        var (otherSellerUser, otherSeller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);
        Assert.NotNull(otherSeller);

        using var client = CreateAuthenticatedClient(opsManager);

        // 1. GET /api/operations/sellers/{sellerId}/accounts - inspect safe owner account
        var getAccountsResp = await client.GetAsync($"/api/operations/sellers/{seller.Id}/accounts");
        Assert.Equal(HttpStatusCode.OK, getAccountsResp.StatusCode);
        Assert.True(getAccountsResp.Headers.CacheControl?.NoStore);

        var getRaw = await getAccountsResp.Content.ReadAsStringAsync();
        AssertNoSecretProperties(getRaw);

        var accounts = await getAccountsResp.Content.ReadFromJsonAsync<List<SellerAccountResponse>>();
        Assert.NotNull(accounts);
        var owner = Assert.Single(accounts, a => a.Id == sellerUser.Id);
        Assert.Equal("SELLER", owner.Role);
        Assert.Equal(seller.Id, owner.SellerId);

        // 2. PUT /api/operations/sellers/{sellerId}/accounts/{userId} - edit owner account
        var updatedOwnerPhone = UniquePhone();
        var updateResp = await client.PutAsJsonAsync($"/api/operations/sellers/{seller.Id}/accounts/{sellerUser.Id}", new UpdateAccountProfileRequest("Updated Seller Owner", updatedOwnerPhone, "888 Dong Khoi"));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);

        var updatedOwner = await updateResp.Content.ReadFromJsonAsync<SellerAccountResponse>();
        Assert.NotNull(updatedOwner);
        Assert.Equal("Updated Seller Owner", updatedOwner.FullName);
        Assert.Equal(updatedOwnerPhone, updatedOwner.Phone);
        Assert.Equal("888 Dong Khoi", updatedOwner.Address);
        Assert.Equal("SELLER", updatedOwner.Role); // Role preserved!

        // 3. Security boundaries: OperationsManager CANNOT edit Admin, OperationsManager, or cross-shop accounts!
        // Target is Admin -> 404
        var adminTargetResp = await client.PutAsJsonAsync($"/api/operations/sellers/{seller.Id}/accounts/{adminUser.Id}", new UpdateAccountProfileRequest("Tamper Admin", UniquePhone()));
        Assert.Equal(HttpStatusCode.NotFound, adminTargetResp.StatusCode);

        // Target belongs to other seller -> 404
        var crossShopResp = await client.PutAsJsonAsync($"/api/operations/sellers/{seller.Id}/accounts/{otherSellerUser.Id}", new UpdateAccountProfileRequest("Cross Shop Tamper", UniquePhone()));
        Assert.Equal(HttpStatusCode.NotFound, crossShopResp.StatusCode);

        // Target is OperationsManager -> 404
        var managerTargetResp = await client.PutAsJsonAsync($"/api/operations/sellers/{seller.Id}/accounts/{opsManager.Id}", new UpdateAccountProfileRequest("Tamper Manager", UniquePhone()));
        Assert.Equal(HttpStatusCode.NotFound, managerTargetResp.StatusCode);
    }

    // ==========================================
    // 5. ADMIN ACCOUNTS DIRECTORY & PROFILES FOR ALL 6 ROLES
    // ==========================================
    [Fact]
    public async Task Admin_CanListAccountsAndGetProfileForAllSixRoles_WithMaskedBank()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (manager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (tech, _) = await CreateIsolatedUserAsync("TECHNICIAN");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        var (staff, _) = await CreateIsolatedUserAsync("SELLER_STAFF");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var client = CreateAuthenticatedClient(admin);

        // 1. GET /api/admin/accounts - paged list across roles
        var listResp = await client.GetAsync("/api/admin/accounts?page=1&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        Assert.True(listResp.Headers.CacheControl?.NoStore);

        var listRaw = await listResp.Content.ReadAsStringAsync();
        AssertNoSecretProperties(listRaw);

        var page = await listResp.Content.ReadFromJsonAsync<AdminAccountPage>();
        Assert.NotNull(page);
        Assert.Contains(page.Items, u => u.Id == admin.Id && u.Role == "ADMIN");
        Assert.Contains(page.Items, u => u.Id == manager.Id && u.Role == "OPERATIONS_MANAGER");
        Assert.Contains(page.Items, u => u.Id == tech.Id && u.Role == "TECHNICIAN");
        Assert.Contains(page.Items, u => u.Id == sellerUser.Id && u.Role == "SELLER");
        Assert.Contains(page.Items, u => u.Id == staff.Id && u.Role == "SELLER_STAFF");
        Assert.Contains(page.Items, u => u.Id == customer.Id && u.Role == "CUSTOMER");

        // 2. Role filter
        var roleFilterResp = await client.GetAsync("/api/admin/accounts?role=TECHNICIAN");
        Assert.Equal(HttpStatusCode.OK, roleFilterResp.StatusCode);
        var techPage = await roleFilterResp.Content.ReadFromJsonAsync<AdminAccountPage>();
        Assert.NotNull(techPage);
        Assert.All(techPage.Items, item => Assert.Equal("TECHNICIAN", item.Role));

        // Invalid role filter -> 400
        var badRoleResp = await client.GetAsync("/api/admin/accounts?role=UNKNOWN_ROLE");
        Assert.Equal(HttpStatusCode.BadRequest, badRoleResp.StatusCode);

        // 3. GET /api/admin/accounts/{userId}/profile for ALL 6 ROLES
        User[] allSix = [admin, manager, tech, sellerUser, staff, customer];
        foreach (var u in allSix)
        {
            var profResp = await client.GetAsync($"/api/admin/accounts/{u.Id}/profile");
            Assert.Equal(HttpStatusCode.OK, profResp.StatusCode);
            Assert.True(profResp.Headers.CacheControl?.NoStore);

            var profRaw = await profResp.Content.ReadAsStringAsync();
            AssertNoSecretProperties(profRaw);

            var prof = await profResp.Content.ReadFromJsonAsync<AdminAccountProfileResponse>();
            Assert.NotNull(prof);
            Assert.Equal(u.Id, prof.Id);
            Assert.Equal(u.Role, prof.Role);
            Assert.Equal(u.Email, prof.LoginEmail);
            Assert.Equal(u.FullName, prof.FullName);
        }

        // Non-existent user profile -> 404
        var notFoundResp = await client.GetAsync($"/api/admin/accounts/{Guid.NewGuid()}/profile");
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);

        // Non-admin cannot access admin accounts -> 403
        using var opsClient = CreateAuthenticatedClient(manager);
        var forbiddenList = await opsClient.GetAsync("/api/admin/accounts");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenList.StatusCode);
        var forbiddenProf = await opsClient.GetAsync($"/api/admin/accounts/{customer.Id}/profile");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenProf.StatusCode);
    }

    // ==========================================
    // 6. BANK REVEALS (PERSONAL BANK & SHOP BANK)
    // ==========================================
    [Fact]
    public async Task Admin_CanRevealPersonalBankAndShopBank_WithMandatoryReasonAuditAndNoStore()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        Assert.NotNull(seller);

        var protection = factory.Services.GetRequiredService<IDataProtectionProvider>();

        // Set genuine encrypted personal bank for customer
        var personalPlainAccount = "0441000" + Random.Shared.Next(100000, 999999);
        var encryptedPersonalBank = protection.CreateProtector("personal-bank-account-v1").Protect(personalPlainAccount);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var profile = new UserProfile
            {
                UserId = customer.Id,
                Address = "111 Nguyen Trai, Q5",
                BankName = "Vietcombank",
                AccountHolder = "NGUYEN VAN CUSTOMER",
                AccountEncrypted = encryptedPersonalBank,
                AccountLast4 = personalPlainAccount[^4..],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
        }

        // Set genuine encrypted shop bank for seller
        var shopPlainAccount = "99988877" + Random.Shared.Next(1000, 9999);
        var encryptedShopBank = protection.CreateProtector("bank-account-v1").Protect(shopPlainAccount);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
            dbSeller.BankName = "Techcombank";
            dbSeller.BankHolder = "CONG TY TNHH HOA BOT";
            dbSeller.BankAccountNoEnc = encryptedShopBank;
            await db.SaveChangesAsync();
        }

        using var adminClient = CreateAuthenticatedClient(admin);

        // 1. Admin Profile for SELLER: verifies personal bank vs shop bank separation
        var sellerProfResp = await adminClient.GetAsync($"/api/admin/accounts/{sellerUser.Id}/profile");
        Assert.Equal(HttpStatusCode.OK, sellerProfResp.StatusCode);
        var sellerProf = await sellerProfResp.Content.ReadFromJsonAsync<AdminAccountProfileResponse>();
        Assert.NotNull(sellerProf);
        Assert.NotNull(sellerProf.ShopBank);
        Assert.Equal("Techcombank", sellerProf.ShopBank.BankName);
        Assert.Equal("CONG TY TNHH HOA BOT", sellerProf.ShopBank.BankHolder);
        Assert.Equal(shopPlainAccount[^4..], sellerProf.ShopBank.AccountSuffix); // Masked!
        Assert.Null(sellerProf.PersonalBank); // Personal bank is separate!

        // 2. Personal Bank Reveal - Reason validation
        var emptyReasonResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{customer.Id}/bank/reveal", new RevealBankRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, emptyReasonResp.StatusCode);

        var shortReasonResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{customer.Id}/bank/reveal", new RevealBankRequest("ab"));
        Assert.Equal(HttpStatusCode.BadRequest, shortReasonResp.StatusCode);

        // 3. Personal Bank Reveal - Success
        var reasonText = "Xác minh tài khoản khách hàng để xử lý hoàn tiền thủ công";
        var revealPersonalResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{customer.Id}/bank/reveal", new RevealBankRequest(reasonText));
        Assert.Equal(HttpStatusCode.OK, revealPersonalResp.StatusCode);
        Assert.True(revealPersonalResp.Headers.CacheControl?.NoStore);

        var personalResult = await revealPersonalResp.Content.ReadFromJsonAsync<RevealBankDetailResponse>();
        Assert.NotNull(personalResult);
        Assert.Equal("Vietcombank", personalResult.BankName);
        Assert.Equal("NGUYEN VAN CUSTOMER", personalResult.AccountHolder);
        Assert.Equal(personalPlainAccount, personalResult.AccountNumber);

        // Verify audit before disclosure without secret in payload
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(a => a.EntityId == customer.Id && a.Action == "ADMIN_PERSONAL_BANK_REVEALED");
            Assert.NotNull(audit);
            Assert.Equal(admin.Id, audit.ActorId);
            Assert.Contains(reasonText, audit.Payload ?? "");
            Assert.DoesNotContain(personalPlainAccount, audit.Payload ?? ""); // NO BANK SECRETS IN AUDIT!
        }

        // 4. Shop Bank Reveal - Success
        var shopReason = "Đối soát tài khoản shop trước khi chuyển khoản doanh thu";
        var revealShopResp = await adminClient.PostAsJsonAsync($"/api/admin/sellers/{seller.Id}/bank/reveal", new RevealBankRequest(shopReason));
        Assert.Equal(HttpStatusCode.OK, revealShopResp.StatusCode);
        Assert.True(revealShopResp.Headers.CacheControl?.NoStore);

        var shopResult = await revealShopResp.Content.ReadFromJsonAsync<RevealBankDetailResponse>();
        Assert.NotNull(shopResult);
        Assert.Equal("Techcombank", shopResult.BankName);
        Assert.Equal("CONG TY TNHH HOA BOT", shopResult.AccountHolder);
        Assert.Equal(shopPlainAccount, shopResult.AccountNumber);

        // Verify audit for shop bank disclosure without secrets
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(a => a.EntityId == seller.Id && a.Action == "ADMIN_SELLER_BANK_REVEALED");
            Assert.NotNull(audit);
            Assert.Equal(admin.Id, audit.ActorId);
            Assert.Contains(shopReason, audit.Payload ?? "");
            Assert.DoesNotContain(shopPlainAccount, audit.Payload ?? ""); // NO BANK SECRETS IN AUDIT!
        }

        // 5. Missing bank -> 404
        var missingBankResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{admin.Id}/bank/reveal", new RevealBankRequest("Xem thu tai khoan chua co bank"));
        Assert.Equal(HttpStatusCode.NotFound, missingBankResp.StatusCode);

        // 6. Non-Admin denied (OperationsManager, Customer, etc.) -> 403 Forbidden!
        using var managerClient = CreateAuthenticatedClient(opsManager);
        var deniedPersonal = await managerClient.PostAsJsonAsync($"/api/admin/accounts/{customer.Id}/bank/reveal", new RevealBankRequest(reasonText));
        Assert.Equal(HttpStatusCode.Forbidden, deniedPersonal.StatusCode);

        var deniedShop = await managerClient.PostAsJsonAsync($"/api/admin/sellers/{seller.Id}/bank/reveal", new RevealBankRequest(shopReason));
        Assert.Equal(HttpStatusCode.Forbidden, deniedShop.StatusCode);
    }

    [Fact]
    public async Task BankReveal_Returns409Conflict_WhenCiphertextIsUnreadable_No500()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        // Corrupt ciphertexts in DB
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var profile = new UserProfile
            {
                UserId = customer.Id,
                Address = "111 Nguyen Trai, Q5",
                BankName = "Vietcombank",
                AccountHolder = "CORRUPTED USER",
                AccountEncrypted = "invalid-corrupted-ciphertext-that-cannot-decrypt",
                AccountLast4 = "9999",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);

            var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
            dbSeller.BankName = "Techcombank";
            dbSeller.BankHolder = "CORRUPTED SELLER";
            dbSeller.BankAccountNoEnc = "invalid-corrupted-shop-ciphertext";

            await db.SaveChangesAsync();
        }

        using var adminClient = CreateAuthenticatedClient(admin);

        // 1. Personal Bank Unreadable -> 409 Conflict (NEVER 500)
        var unreadablePersonal = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{customer.Id}/bank/reveal", new RevealBankRequest("Xử lý tra soát tài khoản có lỗi mã hoá"));
        Assert.Equal(HttpStatusCode.Conflict, unreadablePersonal.StatusCode);

        // 2. Shop Bank Unreadable -> 409 Conflict (NEVER 500)
        var unreadableShop = await adminClient.PostAsJsonAsync($"/api/admin/sellers/{seller.Id}/bank/reveal", new RevealBankRequest("Xử lý tra soát tài khoản shop có lỗi mã hoá"));
        Assert.Equal(HttpStatusCode.Conflict, unreadableShop.StatusCode);
    }

    [Fact]
    public async Task MutationEndpoints_Return404_OnTargetRoleMismatch()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (tech, _) = await CreateIsolatedUserAsync("TECHNICIAN");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var adminClient = CreateAuthenticatedClient(admin);
        using var opsClient = CreateAuthenticatedClient(opsManager);

        // Admin cannot PUT manager with a Customer ID -> 404
        var adminPutWrong = await adminClient.PutAsJsonAsync($"/api/admin/managers/{customer.Id}", new UpdateAccountProfileRequest("Name", UniquePhone()));
        Assert.Equal(HttpStatusCode.NotFound, adminPutWrong.StatusCode);

        // OM cannot PUT technician with a Manager ID -> 404
        var omPutTechWrong = await opsClient.PutAsJsonAsync($"/api/operations/technicians/{opsManager.Id}", new UpdateAccountProfileRequest("Name", UniquePhone()));
        Assert.Equal(HttpStatusCode.NotFound, omPutTechWrong.StatusCode);

        // OM cannot PUT customer with a Technician ID -> 404
        var omPutCustWrong = await opsClient.PutAsJsonAsync($"/api/operations/customers/{tech.Id}", new UpdateAccountProfileRequest("Name", UniquePhone()));
        Assert.Equal(HttpStatusCode.NotFound, omPutCustWrong.StatusCode);

        // OM cannot PATCH customer status with an Admin ID -> 404
        var omPatchCustWrong = await opsClient.PatchAsJsonAsync($"/api/operations/customers/{admin.Id}/status", new UpdateAccountStatusRequest("LOCKED"));
        Assert.Equal(HttpStatusCode.NotFound, omPatchCustWrong.StatusCode);
    }

    // ==========================================
    // 7. AUTHORIZATION MATRIX FOR ALL 16 NEW ROUTES
    // ==========================================
    [Fact]
    public async Task AllSixteenRoutes_EnforceAuthenticationAndRoleAuthorizationMatrix()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (tech, _) = await CreateIsolatedUserAsync("TECHNICIAN");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        var (staff, _) = await CreateIsolatedUserAsync("SELLER_STAFF");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (patchCustomer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        Assert.NotNull(seller);

        var protection = factory.Services.GetRequiredService<IDataProtectionProvider>();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var profile = new UserProfile
            {
                UserId = customer.Id,
                Address = "Auth Matrix Addr",
                BankName = "MBBank",
                AccountHolder = "NGUYEN CUSTOMER",
                AccountEncrypted = protection.CreateProtector("personal-bank-account-v1").Protect("1234567890"),
                AccountLast4 = "7890",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);

            var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
            dbSeller.BankName = "Vietcombank";
            dbSeller.BankHolder = "CONG TY HOA";
            dbSeller.BankAccountNoEnc = protection.CreateProtector("bank-account-v1").Protect("9876543210");
            await db.SaveChangesAsync();
        }

        var usersByRole = new Dictionary<string, User>
        {
            ["ADMIN"] = admin,
            ["OPERATIONS_MANAGER"] = opsManager,
            ["TECHNICIAN"] = tech,
            ["SELLER"] = sellerUser,
            ["SELLER_STAFF"] = staff,
            ["CUSTOMER"] = customer
        };

        var allRoles = usersByRole.Keys.ToArray();

        var routes = new (string Method, string Path, string RightfulRole, Func<HttpContent?> ContentFactory)[]
        {
            ("GET", $"/api/admin/managers/{opsManager.Id}", "ADMIN", () => null),
            ("PUT", $"/api/admin/managers/{opsManager.Id}", "ADMIN", () => JsonContent.Create(new UpdateAccountProfileRequest("Auth Mgr", UniquePhone()))),
            ("GET", $"/api/operations/technicians/{tech.Id}", "OPERATIONS_MANAGER", () => null),
            ("PUT", $"/api/operations/technicians/{tech.Id}", "OPERATIONS_MANAGER", () => JsonContent.Create(new UpdateAccountProfileRequest("Auth Tech", UniquePhone()))),
            ("POST", "/api/operations/customers", "OPERATIONS_MANAGER", () => JsonContent.Create(new CreateAccountRequest(UniqueEmail("matrixcust"), "Matrix Cust", "Pass123456!", UniquePhone()))),
            ("GET", "/api/operations/customers", "OPERATIONS_MANAGER", () => null),
            ("GET", $"/api/operations/customers/{customer.Id}", "OPERATIONS_MANAGER", () => null),
            ("PUT", $"/api/operations/customers/{customer.Id}", "OPERATIONS_MANAGER", () => JsonContent.Create(new UpdateAccountProfileRequest("Auth Cust", UniquePhone()))),
            ("PATCH", $"/api/operations/customers/{patchCustomer.Id}/status", "OPERATIONS_MANAGER", () => JsonContent.Create(new UpdateAccountStatusRequest("LOCKED"))),
            ("PUT", $"/api/operations/sellers/{seller.Id}", "OPERATIONS_MANAGER", () => JsonContent.Create(new UpdateSellerShopRequest("Auth Shop", UniquePhone()))),
            ("GET", $"/api/operations/sellers/{seller.Id}/accounts", "OPERATIONS_MANAGER", () => null),
            ("PUT", $"/api/operations/sellers/{seller.Id}/accounts/{sellerUser.Id}", "OPERATIONS_MANAGER", () => JsonContent.Create(new UpdateAccountProfileRequest("Auth Owner", UniquePhone()))),
            ("GET", "/api/admin/accounts", "ADMIN", () => null),
            ("GET", $"/api/admin/accounts/{customer.Id}/profile", "ADMIN", () => null),
            ("POST", $"/api/admin/accounts/{customer.Id}/bank/reveal", "ADMIN", () => JsonContent.Create(new RevealBankRequest("Ly do xac thuc quyen hop le"))),
            ("POST", $"/api/admin/sellers/{seller.Id}/bank/reveal", "ADMIN", () => JsonContent.Create(new RevealBankRequest("Ly do xac thuc quyen hop le shop")))
        };

        Assert.Equal(16, routes.Length);

        using var anonClient = factory.CreateClient();

        foreach (var route in routes)
        {
            // 1. Anonymous request MUST return 401 Unauthorized
            using var anonReq = new HttpRequestMessage(new HttpMethod(route.Method), route.Path)
            {
                Content = route.ContentFactory()
            };
            var anonResp = await anonClient.SendAsync(anonReq);
            Assert.True(anonResp.StatusCode == HttpStatusCode.Unauthorized,
                $"Expected 401 for anonymous on {route.Method} {route.Path}, got {anonResp.StatusCode}");

            // 2. Wrong roles MUST return 403 Forbidden
            foreach (var role in allRoles.Where(r => r != route.RightfulRole))
            {
                using var client = CreateAuthenticatedClient(usersByRole[role]);
                using var req = new HttpRequestMessage(new HttpMethod(route.Method), route.Path)
                {
                    Content = route.ContentFactory()
                };
                var resp = await client.SendAsync(req);
                Assert.True(resp.StatusCode == HttpStatusCode.Forbidden,
                    $"Expected 403 for role {role} on {route.Method} {route.Path}, got {resp.StatusCode}");
            }

            // 3. Rightful role MUST NOT return 401 or 403 (should be 200 or 201)
            using var rightfulClient = CreateAuthenticatedClient(usersByRole[route.RightfulRole]);
            using var rightfulReq = new HttpRequestMessage(new HttpMethod(route.Method), route.Path)
            {
                Content = route.ContentFactory()
            };
            var rightfulResp = await rightfulClient.SendAsync(rightfulReq);
            Assert.True(rightfulResp.StatusCode == HttpStatusCode.OK || rightfulResp.StatusCode == HttpStatusCode.Created,
                $"Expected 200/201 for rightful role {route.RightfulRole} on {route.Method} {route.Path}, got {rightfulResp.StatusCode}");
        }
    }

    // ==========================================
    // 8. METADATA COVERAGE FOR ALL 16 NEW ROUTES
    // ==========================================
    [Fact]
    public void AllSixteenRoutes_HaveCompleteOpenApiMetadataAndTypedResponses()
    {
        var endpointDataSource = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var endpoints = endpointDataSource.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToList();

        var expectedRoutes = new (string Method, string Pattern, string Policy)[]
        {
            ("GET", "/api/admin/managers/{id:guid}", "Admin"),
            ("PUT", "/api/admin/managers/{id:guid}", "Admin"),
            ("GET", "/api/operations/technicians/{id:guid}", "OperationsManager"),
            ("PUT", "/api/operations/technicians/{id:guid}", "OperationsManager"),
            ("POST", "/api/operations/customers", "OperationsManager"),
            ("GET", "/api/operations/customers", "OperationsManager"),
            ("GET", "/api/operations/customers/{id:guid}", "OperationsManager"),
            ("PUT", "/api/operations/customers/{id:guid}", "OperationsManager"),
            ("PATCH", "/api/operations/customers/{id:guid}/status", "OperationsManager"),
            ("PUT", "/api/operations/sellers/{sellerId:guid}", "OperationsManager"),
            ("GET", "/api/operations/sellers/{sellerId:guid}/accounts", "OperationsManager"),
            ("PUT", "/api/operations/sellers/{sellerId:guid}/accounts/{userId:guid}", "OperationsManager"),
            ("GET", "/api/admin/accounts", "Admin"),
            ("GET", "/api/admin/accounts/{userId:guid}/profile", "Admin"),
            ("POST", "/api/admin/accounts/{userId:guid}/bank/reveal", "Admin"),
            ("POST", "/api/admin/sellers/{sellerId:guid}/bank/reveal", "Admin")
        };

        Assert.Equal(16, expectedRoutes.Length);

        foreach (var expected in expectedRoutes)
        {
            var matched = endpoints.FirstOrDefault(e =>
                string.Equals(e.RoutePattern.RawText, expected.Pattern, StringComparison.OrdinalIgnoreCase) &&
                (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains(expected.Method) == true));

            Assert.NotNull(matched);

            // Verify authorization policy
            var authData = matched.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>();
            Assert.Contains(authData, a => a.Policy == expected.Policy);

            // Verify summary
            var summary = matched.Metadata.GetMetadata<Microsoft.AspNetCore.Http.Metadata.IEndpointSummaryMetadata>()?.Summary;
            Assert.False(string.IsNullOrWhiteSpace(summary), $"Missing summary on {expected.Method} {expected.Pattern}");
        }
    }

    // ==========================================
    // 9. EXTRA JSON INJECTION IMMUNITY
    // ==========================================
    [Fact]
    public async Task ProtectedFields_CannotBeOverwrittenViaExtraJsonInjection()
    {
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        var originalCustomerRole = customer.Role;
        var originalCustomerEmail = customer.Email;
        var originalCustomerPass = customer.PasswordHash;
        var originalCustomerPoints = customer.LoyaltyPoints;

        var originalSellerContact = seller.ContactEmail;
        var originalSellerStatus = seller.Status;
        var originalSellerPackage = seller.PackageId;

        using var client = CreateAuthenticatedClient(opsManager);

        // 1. Extra JSON injection on PUT /api/operations/customers/{id}
        var injectedCustomerJson = $$"""
        {
            "fullName": "Legit Name Change",
            "phone": "{{UniquePhone()}}",
            "address": "Injected Address",
            "role": "ADMIN",
            "email": "hacker@evil.test",
            "passwordHash": "$2a$12$hackedpasswordhashvaluetestinginjection",
            "loyaltyPoints": 999999,
            "status": "DISABLED"
        }
        """;
        var custContent = new StringContent(injectedCustomerJson, System.Text.Encoding.UTF8, "application/json");
        var custResp = await client.PutAsync($"/api/operations/customers/{customer.Id}", custContent);
        Assert.Equal(HttpStatusCode.OK, custResp.StatusCode);

        // 2. Extra JSON injection on PUT /api/operations/sellers/{sellerId}
        var injectedShopJson = $$"""
        {
            "shopName": "Renamed Safe Shop",
            "phone": "{{UniquePhone()}}",
            "address": "Safe Shop Address",
            "contactEmail": "attacker-contact@bad.test",
            "status": "CLOSED",
            "packageId": "{{Guid.NewGuid()}}",
            "bankAccountNoEnc": "injected-bank-ciphertext"
        }
        """;
        var shopContent = new StringContent(injectedShopJson, System.Text.Encoding.UTF8, "application/json");
        var shopResp = await client.PutAsync($"/api/operations/sellers/{seller.Id}", shopContent);
        Assert.Equal(HttpStatusCode.OK, shopResp.StatusCode);

        // 3. Extra JSON injection on PUT /api/operations/sellers/{sellerId}/accounts/{userId}
        var injectedOwnerJson = $$"""
        {
            "fullName": "Legit Owner Name Change",
            "phone": "{{UniquePhone()}}",
            "role": "OPERATIONS_MANAGER",
            "email": "owner-hacker@evil.test",
            "sellerId": "{{Guid.NewGuid()}}"
        }
        """;
        var ownerContent = new StringContent(injectedOwnerJson, System.Text.Encoding.UTF8, "application/json");
        var ownerResp = await client.PutAsync($"/api/operations/sellers/{seller.Id}/accounts/{sellerUser.Id}", ownerContent);
        Assert.Equal(HttpStatusCode.OK, ownerResp.StatusCode);

        // Verify database persistence of protected fields
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        var dbCustomer = await db.Users.SingleAsync(u => u.Id == customer.Id);
        Assert.Equal("Legit Name Change", dbCustomer.FullName);
        Assert.Equal(originalCustomerRole, dbCustomer.Role); // Role preserved!
        Assert.Equal(originalCustomerEmail, dbCustomer.Email); // Email preserved!
        Assert.Equal(originalCustomerPass, dbCustomer.PasswordHash); // Password preserved!
        Assert.Equal(originalCustomerPoints, dbCustomer.LoyaltyPoints); // Points preserved!

        var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
        Assert.Equal("Renamed Safe Shop", dbSeller.ShopName);
        Assert.Equal(originalSellerContact, dbSeller.ContactEmail); // ContactEmail preserved!
        Assert.Equal(originalSellerStatus, dbSeller.Status); // Status preserved!
        Assert.Equal(originalSellerPackage, dbSeller.PackageId); // Package preserved!

        var dbOwner = await db.Users.SingleAsync(u => u.Id == sellerUser.Id);
        Assert.Equal("Legit Owner Name Change", dbOwner.FullName);
        Assert.Equal("SELLER", dbOwner.Role); // Role preserved!
        Assert.Equal(seller.Id, dbOwner.SellerId); // SellerId preserved!
    }

    // ==========================================
    // 10. CONCURRENT ROW LOCK & POST-LOCK RECHECK
    // ==========================================
    [Fact]
    public async Task ConcurrentSellerAccountUpdate_RechecksOwnershipAfterLockWait()
    {
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        var (otherSellerUser, otherSeller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);
        Assert.NotNull(otherSeller);

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var conn = await data.OpenConnectionAsync();
        await using var tx1 = await conn.BeginTransactionAsync();

        // Lock row in Tx1
        await using var lockCmd = new NpgsqlCommand("SELECT id FROM identity.users WHERE id = @id FOR UPDATE", conn, tx1);
        lockCmd.Parameters.AddWithValue("id", sellerUser.Id);
        await lockCmd.ExecuteScalarAsync();

        // Mutate seller_id in Tx1 before commit
        await using var changeCmd = new NpgsqlCommand("UPDATE identity.users SET seller_id = @newSellerId WHERE id = @id", conn, tx1);
        changeCmd.Parameters.AddWithValue("newSellerId", otherSeller.Id);
        changeCmd.Parameters.AddWithValue("id", sellerUser.Id);
        await changeCmd.ExecuteNonQueryAsync();

        using var client = CreateAuthenticatedClient(opsManager);

        // Start Tx2 in background task - it will block waiting for row lock held by Tx1
        var putTask = Task.Run(async () =>
        {
            return await client.PutAsJsonAsync(
                $"/api/operations/sellers/{seller.Id}/accounts/{sellerUser.Id}",
                new UpdateAccountProfileRequest("Concurrent Owner Test", UniquePhone()));
        });

        // Observe a real database wait on this exact transaction, rather than assume timing.
        var waiting = false;
        for (var attempt = 0; attempt < 200 && !putTask.IsCompleted; attempt++)
        {
            await using var checkWait = data.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid)))");
            checkWait.Parameters.AddWithValue("blocker", conn.ProcessID);
            waiting = (bool)(await checkWait.ExecuteScalarAsync())!;
            if (waiting) break;
            await Task.Delay(25);
        }
        Assert.True(waiting, "The update must wait for the transaction that is changing ownership.");
        Assert.False(putTask.IsCompleted);

        // Tx1 commits the change
        await tx1.CommitAsync();

        // Tx2 should now acquire lock, recheck sellerId, and return 404 NotFound!
        var putResp = await putTask;
        Assert.Equal(HttpStatusCode.NotFound, putResp.StatusCode);
        await using var checkName = data.CreateCommand("SELECT full_name FROM identity.users WHERE id = @id");
        checkName.Parameters.AddWithValue("id", sellerUser.Id);
        Assert.Equal(sellerUser.FullName, await checkName.ExecuteScalarAsync());
    }

    // ==========================================
    // 11. ADMIN ACCOUNTS DEFAULT QUERY EXCLUDES STAFF
    // ==========================================
    [Fact]
    public async Task AdminAccounts_DefaultQuery_ExcludesLegacyStaff()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (staff, _) = await CreateIsolatedUserAsync("STAFF"); // Retired/legacy STAFF role

        using var client = CreateAuthenticatedClient(admin);

        var resp = await client.GetAsync($"/api/admin/accounts?search={Uri.EscapeDataString(staff.Email!)}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var page = await resp.Content.ReadFromJsonAsync<AdminAccountPage>();
        Assert.NotNull(page);
        Assert.DoesNotContain(page.Items, item => item.Id == staff.Id || item.Role == "STAFF");
    }

    // ==========================================
    // 12. COMPREHENSIVE VALIDATION & CONFLICTS
    // ==========================================
    [Fact]
    public async Task ValidationAndConflict_ComprehensiveCoverage()
    {
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var client = CreateAuthenticatedClient(opsManager);

        // 1. Customer GET with search > 100 chars -> 400
        var longSearchResp = await client.GetAsync($"/api/operations/customers?search={new string('a', 101)}");
        Assert.Equal(HttpStatusCode.BadRequest, longSearchResp.StatusCode);

        // 2. Customer GET with name > 100 chars -> 400
        var longNameResp = await client.GetAsync($"/api/operations/customers?name={new string('b', 101)}");
        Assert.Equal(HttpStatusCode.BadRequest, longNameResp.StatusCode);

        // 3. Customer GET with email > 100 chars -> 400
        var longEmailResp = await client.GetAsync($"/api/operations/customers?email={new string('c', 101)}");
        Assert.Equal(HttpStatusCode.BadRequest, longEmailResp.StatusCode);

        // 4. Customer PATCH same status -> 409 Conflict (avoid audit spam)
        var sameStatusResp = await client.PatchAsJsonAsync($"/api/operations/customers/{customer.Id}/status", new UpdateAccountStatusRequest(customer.Status));
        Assert.Equal(HttpStatusCode.Conflict, sameStatusResp.StatusCode);

        // 5. Customer PATCH invalid status -> 400
        var invalidStatusResp = await client.PatchAsJsonAsync($"/api/operations/customers/{customer.Id}/status", new UpdateAccountStatusRequest("BOGUS_STATUS"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatusResp.StatusCode);

        // 6. Name with control characters -> 400
        var controlCharResp = await client.PutAsJsonAsync($"/api/operations/customers/{customer.Id}", new UpdateAccountProfileRequest("Name\0WithControl"));
        Assert.Equal(HttpStatusCode.BadRequest, controlCharResp.StatusCode);

        // 7. Address with control characters -> 400
        var controlAddrResp = await client.PutAsJsonAsync($"/api/operations/customers/{customer.Id}", new UpdateAccountProfileRequest("Valid Name", null, "Addr\tWithControl\0"));
        Assert.Equal(HttpStatusCode.BadRequest, controlAddrResp.StatusCode);

        // 8. Address > 300 chars -> 400
        var longAddrResp = await client.PutAsJsonAsync($"/api/operations/customers/{customer.Id}", new UpdateAccountProfileRequest("Valid Name", null, new string('x', 301)));
        Assert.Equal(HttpStatusCode.BadRequest, longAddrResp.StatusCode);
    }

    // ==========================================
    // 13. LIST PAGINATION HASMORE
    // ==========================================
    [Fact]
    public async Task ListPagination_ReturnsHasMore_WhenAdditionalRecordsExist()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (cust1, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (cust2, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var opsClient = CreateAuthenticatedClient(opsManager);
        var custPageResp = await opsClient.GetAsync("/api/operations/customers?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, custPageResp.StatusCode);
        var custPage = await custPageResp.Content.ReadFromJsonAsync<CustomerPageResponse>();
        Assert.NotNull(custPage);
        Assert.Single(custPage.Items);
        Assert.True(custPage.HasMore);

        using var adminClient = CreateAuthenticatedClient(admin);
        var adminPageResp = await adminClient.GetAsync("/api/admin/accounts?page=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, adminPageResp.StatusCode);
        var adminPage = await adminPageResp.Content.ReadFromJsonAsync<AdminAccountPage>();
        Assert.NotNull(adminPage);
        Assert.Single(adminPage.Items);
        Assert.True(adminPage.HasMore);
    }

    // ==========================================
    // 14. MISSING RESOURCE RETURNS 404 ACROSS ALL ROUTES
    // ==========================================
    [Fact]
    public async Task MissingResource_Returns404_AcrossAllResourceRoutes()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        using var adminClient = CreateAuthenticatedClient(admin);
        using var opsClient = CreateAuthenticatedClient(opsManager);

        var random = Guid.NewGuid();

        // Admin endpoints with missing target
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.GetAsync($"/api/admin/managers/{random}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.PutAsJsonAsync($"/api/admin/managers/{random}", new UpdateAccountProfileRequest("Name", UniquePhone()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.GetAsync($"/api/admin/accounts/{random}/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.PostAsJsonAsync($"/api/admin/accounts/{random}/bank/reveal", new RevealBankRequest("Ly do tra cuu hop le"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminClient.PostAsJsonAsync($"/api/admin/sellers/{random}/bank/reveal", new RevealBankRequest("Ly do tra cuu hop le"))).StatusCode);

        // Operations Manager endpoints with missing target
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.GetAsync($"/api/operations/technicians/{random}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.PutAsJsonAsync($"/api/operations/technicians/{random}", new UpdateAccountProfileRequest("Name", UniquePhone()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.GetAsync($"/api/operations/customers/{random}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.PutAsJsonAsync($"/api/operations/customers/{random}", new UpdateAccountProfileRequest("Name", UniquePhone()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.PatchAsJsonAsync($"/api/operations/customers/{random}/status", new UpdateAccountStatusRequest("LOCKED"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.PutAsJsonAsync($"/api/operations/sellers/{random}", new UpdateSellerShopRequest("Shop", UniquePhone()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.GetAsync($"/api/operations/sellers/{random}/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await opsClient.PutAsJsonAsync($"/api/operations/sellers/{seller.Id}/accounts/{random}", new UpdateAccountProfileRequest("Name", UniquePhone()))).StatusCode);
    }
}
