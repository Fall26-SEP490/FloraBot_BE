using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SellerStaffManagementTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Guid Shop1 = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Shop2 = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Seller1User = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Seller2User = Guid.Parse("10000000-0000-0000-0000-000000000004");
    private static readonly Guid AdminUser = Guid.Parse("10000000-0000-0000-0000-000000000001");

    private static string UniquePhone() => $"09{Random.Shared.Next(10000000, 99999999)}";
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@invalid.test";

    private async Task<Guid> SeedUserAsync(string role, Guid? sellerId = null)
    {
        var id = Guid.NewGuid();
        var email = UniqueEmail(role.ToLowerInvariant());
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var cmd = data.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, phone, role, status, seller_id) " +
            "VALUES(@id, @email, '$2b$12$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', @name, @phone, @role, 'ACTIVE', @sellerId)");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("name", $"{role} Test User");
        cmd.Parameters.AddWithValue("phone", UniquePhone());
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("sellerId", (object?)sellerId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    [Fact]
    public async Task OwnLockedAndDisabledStaffAreVisibleInAccountsDirectory_AndPickerRemainsActiveOnly()
    {
        using var client = factory.CreateClient();
        var sellerToken = factory.Token("SELLER", userId: Seller1User, sellerId: Shop1);
        client.DefaultRequestHeaders.Authorization = new("Bearer", sellerToken);

        // 1. Create 3 staff members for Shop 1
        var email1 = UniqueEmail("staff-active");
        var res1 = await client.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest(email1, "Active Delivery Staff", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var staff1 = (await res1.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;

        var email2 = UniqueEmail("staff-locked");
        var res2 = await client.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest(email2, "Locked Delivery Staff", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, res2.StatusCode);
        var staff2 = (await res2.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;

        var email3 = UniqueEmail("staff-disabled");
        var res3 = await client.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest(email3, "Disabled Delivery Staff", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, res3.StatusCode);
        var staff3 = (await res3.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;

        // 2. Transition staff2 to LOCKED, staff3 to DISABLED
        var lockRes = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff2.Id}/status", new UpdateAccountStatusRequest("LOCKED"));
        Assert.Equal(HttpStatusCode.OK, lockRes.StatusCode);
        var lockedItem = (await lockRes.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;
        Assert.Equal("LOCKED", lockedItem.Status);

        var disableRes = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff3.Id}/status", new UpdateAccountStatusRequest("DISABLED"));
        Assert.Equal(HttpStatusCode.OK, disableRes.StatusCode);
        var disabledItem = (await disableRes.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;
        Assert.Equal("DISABLED", disabledItem.Status);

        // 3. GET /api/sellers/{sellerId}/staff/accounts returns ALL own SELLER_STAFF statuses (ACTIVE, LOCKED, DISABLED)
        var accountsRes = await client.GetAsync($"/api/sellers/{Shop1}/staff/accounts");
        Assert.Equal(HttpStatusCode.OK, accountsRes.StatusCode);
        Assert.True(accountsRes.Headers.CacheControl?.NoStore == true, "New endpoint must set Cache-Control: no-store");

        var rawJson = await accountsRes.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(rawJson);
        Assert.Equal(JsonValueKind.Array, jsonDoc.RootElement.ValueKind);

        // Verify no password hash or secrets leaked in directory items
        foreach (var element in jsonDoc.RootElement.EnumerateArray())
        {
            Assert.False(element.TryGetProperty("passwordHash", out _));
            Assert.False(element.TryGetProperty("password_hash", out _));
            Assert.False(element.TryGetProperty("password", out _));
            Assert.False(element.TryGetProperty("hash", out _));
        }

        var accountsList = JsonSerializer.Deserialize<List<AccountDirectoryItem>>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var found1 = Assert.Single(accountsList, x => x.Id == staff1.Id);
        Assert.Equal("ACTIVE", found1.Status);
        Assert.Equal("SELLER_STAFF", found1.Role);
        Assert.Equal(email1, found1.Email);

        var found2 = Assert.Single(accountsList, x => x.Id == staff2.Id);
        Assert.Equal("LOCKED", found2.Status);
        Assert.Equal("SELLER_STAFF", found2.Role);
        Assert.Equal(email2, found2.Email);

        var found3 = Assert.Single(accountsList, x => x.Id == staff3.Id);
        Assert.Equal("DISABLED", found3.Status);
        Assert.Equal("SELLER_STAFF", found3.Role);
        Assert.Equal(email3, found3.Email);

        // 4. GET /api/sellers/{sellerId}/staff in KioskOps remains ACTIVE-only task picker
        var pickerRes = await client.GetAsync($"/api/sellers/{Shop1}/staff");
        Assert.Equal(HttpStatusCode.OK, pickerRes.StatusCode);
        var pickerList = (await pickerRes.Content.ReadFromJsonAsync<List<StaffDirectoryItem>>())!;

        Assert.Contains(pickerList, x => x.Id == staff1.Id);
        Assert.DoesNotContain(pickerList, x => x.Id == staff2.Id);
        Assert.DoesNotContain(pickerList, x => x.Id == staff3.Id);
    }

    [Fact]
    public async Task RestoreActiveStatus_ReenablesStaffInAssignmentPicker()
    {
        using var client = factory.CreateClient();
        var sellerToken = factory.Token("SELLER", userId: Seller1User, sellerId: Shop1);
        client.DefaultRequestHeaders.Authorization = new("Bearer", sellerToken);

        // 1. Create a staff member
        var email = UniqueEmail("staff-restore");
        var res = await client.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest(email, "Restorable Staff", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var staff = (await res.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;

        // 2. Lock staff and verify exclusion from picker
        (await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff.Id}/status", new UpdateAccountStatusRequest("LOCKED"))).EnsureSuccessStatusCode();
        var pickerLocked = (await client.GetFromJsonAsync<List<StaffDirectoryItem>>($"/api/sellers/{Shop1}/staff"))!;
        Assert.DoesNotContain(pickerLocked, x => x.Id == staff.Id);

        // 3. Restore to ACTIVE and verify inclusion in picker
        var restoreFromLock = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff.Id}/status", new UpdateAccountStatusRequest("ACTIVE"));
        Assert.Equal(HttpStatusCode.OK, restoreFromLock.StatusCode);
        var restoredItem1 = (await restoreFromLock.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;
        Assert.Equal("ACTIVE", restoredItem1.Status);

        var pickerRestored1 = (await client.GetFromJsonAsync<List<StaffDirectoryItem>>($"/api/sellers/{Shop1}/staff"))!;
        Assert.Contains(pickerRestored1, x => x.Id == staff.Id);

        // 4. Disable staff and verify exclusion from picker
        (await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff.Id}/status", new UpdateAccountStatusRequest("DISABLED"))).EnsureSuccessStatusCode();
        var pickerDisabled = (await client.GetFromJsonAsync<List<StaffDirectoryItem>>($"/api/sellers/{Shop1}/staff"))!;
        Assert.DoesNotContain(pickerDisabled, x => x.Id == staff.Id);

        // 5. Restore to ACTIVE and verify inclusion in picker
        var restoreFromDisable = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff.Id}/status", new UpdateAccountStatusRequest("ACTIVE"));
        Assert.Equal(HttpStatusCode.OK, restoreFromDisable.StatusCode);
        var restoredItem2 = (await restoreFromDisable.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;
        Assert.Equal("ACTIVE", restoredItem2.Status);

        var pickerRestored2 = (await client.GetFromJsonAsync<List<StaffDirectoryItem>>($"/api/sellers/{Shop1}/staff"))!;
        Assert.Contains(pickerRestored2, x => x.Id == staff.Id);
    }

    [Fact]
    public async Task WrongTenantReturns404ConcealingForeignShopExistence()
    {
        using var client = factory.CreateClient();
        var sellerToken = factory.Token("SELLER", userId: Seller1User, sellerId: Shop1);
        client.DefaultRequestHeaders.Authorization = new("Bearer", sellerToken);

        // Calling foreign shop routes with Shop1 token must yield 404 (SameSeller concealing)
        var getForeignDir = await client.GetAsync($"/api/sellers/{Shop2}/staff/accounts");
        Assert.Equal(HttpStatusCode.NotFound, getForeignDir.StatusCode);

        var postForeign = await client.PostAsJsonAsync($"/api/sellers/{Shop2}/staff", new CreateAccountRequest(UniqueEmail("cross"), "Cross Staff", "StrongPass123!"));
        Assert.Equal(HttpStatusCode.NotFound, postForeign.StatusCode);

        var patchForeign = await client.PatchAsJsonAsync($"/api/sellers/{Shop2}/staff/{Guid.NewGuid()}/status", new UpdateAccountStatusRequest("LOCKED"));
        Assert.Equal(HttpStatusCode.NotFound, patchForeign.StatusCode);
    }

    [Fact]
    public async Task CannotUpdateForeignStaffViaOwnShopUrl()
    {
        using var client = factory.CreateClient();

        // 1. Seller 2 creates staff for Shop 2
        var seller2Token = factory.Token("SELLER", userId: Seller2User, sellerId: Shop2);
        client.DefaultRequestHeaders.Authorization = new("Bearer", seller2Token);

        var email2 = UniqueEmail("staff-shop2");
        var res2 = await client.PostAsJsonAsync($"/api/sellers/{Shop2}/staff", new CreateAccountRequest(email2, "Shop 2 Staff", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, res2.StatusCode);
        var staffShop2 = (await res2.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;

        // 2. Seller 1 attempts to update Shop 2 staff via Shop 1's URL
        var seller1Token = factory.Token("SELLER", userId: Seller1User, sellerId: Shop1);
        client.DefaultRequestHeaders.Authorization = new("Bearer", seller1Token);

        var attackResp = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staffShop2.Id}/status", new UpdateAccountStatusRequest("DISABLED"));
        Assert.Equal(HttpStatusCode.NotFound, attackResp.StatusCode);

        // 3. Verify in DB that Shop 2 staff status is completely unchanged (remains ACTIVE)
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var checkCmd = data.CreateCommand("SELECT status FROM identity.users WHERE id=@id");
        checkCmd.Parameters.AddWithValue("id", staffShop2.Id);
        var actualStatus = (string)(await checkCmd.ExecuteScalarAsync())!;
        Assert.Equal("ACTIVE", actualStatus);
    }

    [Fact]
    public async Task UnauthorizedRolesCannotCreateOrManageShopStaff()
    {
        // 1. Create a staff member under Shop 1 using Seller 1
        using var sellerClient = factory.CreateClient();
        sellerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: Seller1User, sellerId: Shop1));
        var email = UniqueEmail("target-staff");
        var createRes = await sellerClient.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest(email, "Target Staff", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var targetStaff = (await createRes.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;

        // 2. Seed active users for non-SELLER roles
        var opsManagerId = await SeedUserAsync("OPERATIONS_MANAGER");
        var technicianId = await SeedUserAsync("TECHNICIAN");
        var customerId = await SeedUserAsync("CUSTOMER");
        var sellerStaffId = await SeedUserAsync("SELLER_STAFF", Shop1);

        var testRoles = new (string Role, Guid UserId, Guid? SellerId)[]
        {
            ("ADMIN", AdminUser, null),
            ("OPERATIONS_MANAGER", opsManagerId, null),
            ("TECHNICIAN", technicianId, null),
            ("CUSTOMER", customerId, null),
            ("SELLER_STAFF", sellerStaffId, Shop1)
        };

        foreach (var (role, userId, sellerId) in testRoles)
        {
            using var client = factory.CreateClient();
            var token = factory.Token(role, userId: userId, sellerId: sellerId);
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            // ADMIN passes SameSellerRequirement but is rejected by Seller policy -> 403 Forbidden.
            // Non-merchant roles fail SameSellerRequirement -> masked as 404 NotFound by TenantAuthorizationResultHandler.
            var expectedStatus = role == "ADMIN" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;

            // Cannot GET staff accounts directory
            var getResp = await client.GetAsync($"/api/sellers/{Shop1}/staff/accounts");
            Assert.True(getResp.StatusCode == expectedStatus, $"Expected {expectedStatus} for role {role} on GET /staff/accounts, received {(int)getResp.StatusCode}");

            // Cannot POST new staff account
            var postResp = await client.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest(UniqueEmail($"by-{role}"), "Unauthorized Staff", "StrongPass123!"));
            Assert.True(postResp.StatusCode == expectedStatus, $"Expected {expectedStatus} for role {role} on POST /staff, received {(int)postResp.StatusCode}");

            // Cannot PATCH staff status
            var patchResp = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{targetStaff.Id}/status", new UpdateAccountStatusRequest("LOCKED"));
            Assert.True(patchResp.StatusCode == expectedStatus, $"Expected {expectedStatus} for role {role} on PATCH /staff/status, received {(int)patchResp.StatusCode}");
        }
    }

    [Fact]
    public async Task AnonymousRequestsAreRejectedWith401()
    {
        using var client = factory.CreateClient();

        var getResp = await client.GetAsync($"/api/sellers/{Shop1}/staff/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, getResp.StatusCode);

        var postResp = await client.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest("anon@invalid.test", "Anon", "StrongPass123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, postResp.StatusCode);

        var patchResp = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{Guid.NewGuid()}/status", new UpdateAccountStatusRequest("LOCKED"));
        Assert.Equal(HttpStatusCode.Unauthorized, patchResp.StatusCode);
    }

    [Fact]
    public async Task InvalidStatusRequestReturnsBadRequest()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: Seller1User, sellerId: Shop1));

        var res = await client.PostAsJsonAsync($"/api/sellers/{Shop1}/staff", new CreateAccountRequest(UniqueEmail("validation-staff"), "Valid Staff", "StrongPass123!", UniquePhone()));
        var staff = (await res.Content.ReadFromJsonAsync<AccountDirectoryItem>())!;

        var invalidResp = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff.Id}/status", new UpdateAccountStatusRequest("INVALID_STATUS"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidResp.StatusCode);

        var emptyResp = await client.PatchAsJsonAsync($"/api/sellers/{Shop1}/staff/{staff.Id}/status", new UpdateAccountStatusRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, emptyResp.StatusCode);
    }
}
