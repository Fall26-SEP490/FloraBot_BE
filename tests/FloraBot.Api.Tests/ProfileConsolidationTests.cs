using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class ProfileConsolidationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(User User, Seller? Seller)> CreateIsolatedUserAsync(string role, string? email = null)
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
                ShopName = "Consolidated Shop " + Random.Shared.Next(100000, 999999),
                Phone = "09" + Random.Shared.Next(10000000, 99999999),
                Status = "APPROVED",
                PackageId = package.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Sellers.Add(seller);
            await db.SaveChangesAsync();
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email ?? $"cons-{Guid.NewGuid():N}@isolated-test.example",
            Phone = "09" + Random.Shared.Next(10000000, 99999999),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
            FullName = $"Test User {role}",
            Role = role,
            SellerId = seller?.Id,
            Status = "ACTIVE",
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

    // =========================================================================
    // 1. ALL 6 ROLES READ OWN FULL BANK IN SINGLE GET PROFILE CALL
    // =========================================================================
    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER")]
    [InlineData("SELLER_STAFF")]
    [InlineData("CUSTOMER")]
    public async Task AllSixRoles_CanReadOwnFullBank_AndAuditsBeforeResponse(string role)
    {
        var (user, _) = await CreateIsolatedUserAsync(role);
        var protection = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var rawAccount = "0771000" + Random.Shared.Next(100000, 999999);
        var encAccount = protection.CreateProtector("personal-bank-account-v1").Protect(rawAccount);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var profile = new UserProfile
            {
                UserId = user.Id,
                Address = "123 Ly Thuong Kiet",
                BankName = "MBBank",
                AccountHolder = "NGUYEN VAN " + role,
                AccountEncrypted = encAccount,
                AccountLast4 = rawAccount[^4..],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
        }

        using var client = CreateAuthenticatedClient(user);
        var resp = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("no-store", resp.Headers.CacheControl?.ToString());

        var profileResult = await resp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profileResult);
        Assert.NotNull(profileResult.Bank);
        Assert.Equal("MBBank", profileResult.Bank.BankName);
        Assert.Equal("NGUYEN VAN " + role, profileResult.Bank.AccountHolder);
        Assert.Equal(rawAccount[^4..], profileResult.Bank.AccountLast4);
        Assert.True(profileResult.Bank.HasAccount);
        Assert.Equal(rawAccount, profileResult.Bank.AccountNumber); // Full bank number revealed to owner!
        Assert.Equal("READY", profileResult.Bank.Status);

        // Verify audit log has PERSONAL_BANK_REVEALED and no plaintext account number
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var audit = await db.AuditLogs.AsNoTracking()
                .Where(a => a.ActorId == user.Id && a.Action == "PERSONAL_BANK_REVEALED")
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync();
            Assert.NotNull(audit);
            Assert.Equal("user_profiles", audit.EntityType);
            Assert.Equal(user.Id, audit.EntityId);
            Assert.DoesNotContain(rawAccount, audit.Payload ?? "");
            Assert.DoesNotContain(encAccount, audit.Payload ?? "");
        }
    }

    // =========================================================================
    // 2. OTHER USER ACCESS PROHIBITED & NO ID SELECTION ON SELF ROUTE
    // =========================================================================
    [Fact]
    public async Task SelfProfile_HasNoIdSelection_AndNonAdminCannotGetOtherProfiles()
    {
        var (user1, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (user2, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var client1 = CreateAuthenticatedClient(user1);

        // 1. GET /api/account/profile always returns user1 (no route param to select user2)
        var selfResp = await client1.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, selfResp.StatusCode);
        var selfProfile = await selfResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(selfProfile);
        Assert.Equal(user1.Id, selfProfile.Id);

        // 2. Querying other user ID via query params has no effect (still returns user1)
        var queryResp = await client1.GetAsync($"/api/account/profile?userId={user2.Id}");
        Assert.Equal(HttpStatusCode.OK, queryResp.StatusCode);
        var queryProfile = await queryResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(queryProfile);
        Assert.Equal(user1.Id, queryProfile.Id);

        // 3. Non-admin accessing admin profile endpoint gets 403
        var forbiddenResp = await client1.GetAsync($"/api/admin/accounts/{user2.Id}/profile");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);

        // 4. Anonymous access returns 401
        using var anonClient = factory.CreateClient();
        var anonResp = await anonClient.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);
    }

    // =========================================================================
    // 3. ADMIN PROFILE REVEALS BOTH PERSONAL AND SHOP BANK WITH AUDIT
    // =========================================================================
    [Fact]
    public async Task AdminProfile_RevealsBothPersonalAndShopBank_WithAudit_AndManagerDenied()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        var (manager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        Assert.NotNull(seller);

        var protection = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var personalPlainAccount = "0551000" + Random.Shared.Next(100000, 999999);
        var shopPlainAccount = "09988877" + Random.Shared.Next(1000, 9999);

        // Configure personal bank in user_profiles
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var profile = new UserProfile
            {
                UserId = sellerUser.Id,
                Address = "Personal Villa, District 2",
                BankName = "Vietcombank Personal",
                AccountHolder = "LE VAN SELLER",
                AccountEncrypted = protection.CreateProtector("personal-bank-account-v1").Protect(personalPlainAccount),
                AccountLast4 = personalPlainAccount[^4..],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);

            // Configure shop bank in sellers
            var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
            dbSeller.BankName = "Techcombank Shop";
            dbSeller.BankHolder = "CONG TY TNHH FLORA SHOP";
            dbSeller.BankAccountNoEnc = protection.CreateProtector("bank-account-v1").Protect(shopPlainAccount);

            await db.SaveChangesAsync();
        }

        using var adminClient = CreateAuthenticatedClient(admin);
        var profResp = await adminClient.GetAsync($"/api/admin/accounts/{sellerUser.Id}/profile");
        Assert.Equal(HttpStatusCode.OK, profResp.StatusCode);
        Assert.True(profResp.Headers.CacheControl?.NoStore);

        var prof = await profResp.Content.ReadFromJsonAsync<AdminAccountProfileResponse>();
        Assert.NotNull(prof);

        // Personal bank verification
        Assert.NotNull(prof.PersonalBank);
        Assert.Equal("Vietcombank Personal", prof.PersonalBank.BankName);
        Assert.Equal("LE VAN SELLER", prof.PersonalBank.AccountHolder);
        Assert.Equal(personalPlainAccount[^4..], prof.PersonalBank.AccountLast4);
        Assert.Equal(personalPlainAccount, prof.PersonalBank.AccountNumber);
        Assert.Equal("READY", prof.PersonalBank.Status);

        // Shop bank verification
        Assert.NotNull(prof.ShopBank);
        Assert.Equal("Techcombank Shop", prof.ShopBank.BankName);
        Assert.Equal("CONG TY TNHH FLORA SHOP", prof.ShopBank.BankHolder);
        Assert.Equal(shopPlainAccount[^4..], prof.ShopBank.AccountSuffix);
        Assert.Equal(shopPlainAccount, prof.ShopBank.AccountNumber);
        Assert.Equal("READY", prof.ShopBank.Status);

        // Ensure personal and shop bank numbers are distinct
        Assert.NotEqual(prof.PersonalBank.AccountNumber, prof.ShopBank.AccountNumber);

        // Verify audit log has ADMIN_ACCOUNT_PROFILE_VIEWED and no plaintext secrets
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var audit = await db.AuditLogs.OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(a => a.EntityId == sellerUser.Id && a.Action == "ADMIN_ACCOUNT_PROFILE_VIEWED");
            Assert.NotNull(audit);
            Assert.Equal(admin.Id, audit.ActorId);
            Assert.Contains("has_personal_bank", audit.Payload ?? "");
            Assert.Contains("has_shop_bank", audit.Payload ?? "");
            Assert.DoesNotContain(personalPlainAccount, audit.Payload ?? "");
            Assert.DoesNotContain(shopPlainAccount, audit.Payload ?? "");
        }

        // Operations Manager is denied
        using var managerClient = CreateAuthenticatedClient(manager);
        var deniedResp = await managerClient.GetAsync($"/api/admin/accounts/{sellerUser.Id}/profile");
        Assert.Equal(HttpStatusCode.Forbidden, deniedResp.StatusCode);
    }

    // =========================================================================
    // 4. DIRECTORY LISTINGS REMAIN MASKED (NO FULL NUMBERS)
    // =========================================================================
    [Fact]
    public async Task DirectoryListings_RemainMasked_NoFullBankNumbers()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (opsManager, _) = await CreateIsolatedUserAsync("OPERATIONS_MANAGER");
        var protection = factory.Services.GetRequiredService<IDataProtectionProvider>();

        var rawBankNumber = "0881000" + Random.Shared.Next(100000, 999999);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var profile = new UserProfile
            {
                UserId = customer.Id,
                Address = "List Masked Address",
                BankName = "VietinBank",
                AccountHolder = "NGUYEN MASKED",
                AccountEncrypted = protection.CreateProtector("personal-bank-account-v1").Protect(rawBankNumber),
                AccountLast4 = rawBankNumber[^4..],
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
        }

        // 1. Admin account list remains masked
        using var adminClient = CreateAuthenticatedClient(admin);
        var adminListResp = await adminClient.GetAsync($"/api/admin/accounts?search={customer.Phone}");
        Assert.Equal(HttpStatusCode.OK, adminListResp.StatusCode);
        var rawContent = await adminListResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawBankNumber, rawContent); // Never full number in list!

        var page = await adminListResp.Content.ReadFromJsonAsync<AdminAccountPage>();
        Assert.NotNull(page);
        var item = page.Items.FirstOrDefault(i => i.Id == customer.Id);
        Assert.NotNull(item);
        Assert.NotNull(item.Bank);
        Assert.Equal("VietinBank", item.Bank.BankName);
        Assert.Equal(rawBankNumber[^4..], item.Bank.AccountLast4);
        Assert.True(item.Bank.HasBank);

        // 2. Operations Manager customer detail DTO never has full bank
        using var opsClient = CreateAuthenticatedClient(opsManager);
        var opsCustResp = await opsClient.GetAsync($"/api/operations/customers/{customer.Id}");
        Assert.Equal(HttpStatusCode.OK, opsCustResp.StatusCode);
        var opsCustRaw = await opsCustResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawBankNumber, opsCustRaw);
    }

    // =========================================================================
    // 5. REMOVED REVEAL ROUTES RETURN 404 & ABSENT FROM METADATA
    // =========================================================================
    [Fact]
    public async Task RemovedRevealRoutes_Return404_AndAbsentFromOpenApiMetadata()
    {
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        using var adminClient = CreateAuthenticatedClient(admin);

        // Both removed endpoints return 404
        var personalResp = await adminClient.PostAsJsonAsync($"/api/admin/accounts/{customer.Id}/bank/reveal", new { reason = "Test removed" });
        Assert.Equal(HttpStatusCode.NotFound, personalResp.StatusCode);

        var shopResp = await adminClient.PostAsJsonAsync($"/api/admin/sellers/{seller.Id}/bank/reveal", new { reason = "Test removed" });
        Assert.Equal(HttpStatusCode.NotFound, shopResp.StatusCode);

        // Verify absent from RouteEndpoint metadata
        var endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();
        var endpoints = endpointDataSource.Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.DoesNotContain(endpoints, e =>
            string.Equals(e.RoutePattern.RawText, "/api/admin/accounts/{userId:guid}/bank/reveal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(endpoints, e =>
            string.Equals(e.RoutePattern.RawText, "/api/admin/sellers/{sellerId:guid}/bank/reveal", StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // 6. DEMOGRAPHICS: SAVE, READ, CLEAR, OMITTED PRESERVE, VALIDATIONS
    // =========================================================================
    [Fact]
    public async Task Demographics_Save_Read_Clear_PreserveOmitted_Future400_InvalidGender400()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);

        // 1. Save valid Gender and DateOfBirth
        var initialDob = new DateOnly(1998, 8, 15);
        var saveReq = new UpdateUserProfileRequest(
            FullName: "Demographic Test User",
            Phone: user.Phone,
            Address: "Demographic Address",
            Gender: "MALE",
            DateOfBirth: initialDob);

        var saveResp = await client.PutAsJsonAsync("/api/account/profile", saveReq);
        Assert.Equal(HttpStatusCode.OK, saveResp.StatusCode);
        var savedProfile = await saveResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(savedProfile);
        Assert.Equal("MALE", savedProfile.Gender);
        Assert.Equal(initialDob, savedProfile.DateOfBirth);

        // 2. Read back via GET
        var getResp = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var getProfile = await getResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(getProfile);
        Assert.Equal("MALE", getProfile.Gender);
        Assert.Equal(initialDob, getProfile.DateOfBirth);

        // 3. Omitted fields preserve existing values (old FE requests must not erase DOB/gender)
        var updateNameOnlyReq = new UpdateUserProfileRequest(
            FullName: "Renamed User",
            Phone: user.Phone,
            Address: "Updated Address"); // Gender and DateOfBirth omitted (null), no clear flags

        var updateNameResp = await client.PutAsJsonAsync("/api/account/profile", updateNameOnlyReq);
        Assert.Equal(HttpStatusCode.OK, updateNameResp.StatusCode);
        var preservedProfile = await updateNameResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(preservedProfile);
        Assert.Equal("Renamed User", preservedProfile.FullName);
        Assert.Equal("MALE", preservedProfile.Gender); // Preserved!
        Assert.Equal(initialDob, preservedProfile.DateOfBirth); // Preserved!

        // 4. Conflicting clear + value returns 400
        var conflictGenderReq = new UpdateUserProfileRequest(
            FullName: "Renamed User",
            Phone: user.Phone,
            Address: "Updated Address",
            Gender: "FEMALE",
            ClearGender: true);
        var conflictGenderResp = await client.PutAsJsonAsync("/api/account/profile", conflictGenderReq);
        Assert.Equal(HttpStatusCode.BadRequest, conflictGenderResp.StatusCode);

        var conflictDobReq = new UpdateUserProfileRequest(
            FullName: "Renamed User",
            Phone: user.Phone,
            Address: "Updated Address",
            DateOfBirth: initialDob,
            ClearDateOfBirth: true);
        var conflictDobResp = await client.PutAsJsonAsync("/api/account/profile", conflictDobReq);
        Assert.Equal(HttpStatusCode.BadRequest, conflictDobResp.StatusCode);

        // 5. Invalid gender value returns 400
        var invalidGenderReq = new UpdateUserProfileRequest(
            FullName: "Renamed User",
            Phone: user.Phone,
            Address: "Updated Address",
            Gender: "UNKNOWN_GENDER");
        var invalidGenderResp = await client.PutAsJsonAsync("/api/account/profile", invalidGenderReq);
        Assert.Equal(HttpStatusCode.BadRequest, invalidGenderResp.StatusCode);

        // 6. Future date in Vietnam returns 400
        var todayVn = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));
        var futureDobReq = new UpdateUserProfileRequest(
            FullName: "Renamed User",
            Phone: user.Phone,
            Address: "Updated Address",
            DateOfBirth: todayVn.AddDays(1));
        var futureDobResp = await client.PutAsJsonAsync("/api/account/profile", futureDobReq);
        Assert.Equal(HttpStatusCode.BadRequest, futureDobResp.StatusCode);

        // Vietnam today date is valid
        var todayDobReq = new UpdateUserProfileRequest(
            FullName: "Renamed User",
            Phone: user.Phone,
            Address: "Updated Address",
            DateOfBirth: todayVn);
        var todayDobResp = await client.PutAsJsonAsync("/api/account/profile", todayDobReq);
        Assert.Equal(HttpStatusCode.OK, todayDobResp.StatusCode);

        // 7. Explicit clear resets gender and date of birth
        var clearReq = new UpdateUserProfileRequest(
            FullName: "Renamed User",
            Phone: user.Phone,
            Address: "Updated Address",
            ClearGender: true,
            ClearDateOfBirth: true);
        var clearResp = await client.PutAsJsonAsync("/api/account/profile", clearReq);
        Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);
        var clearedProfile = await clearResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(clearedProfile);
        Assert.Null(clearedProfile.Gender);
        Assert.Null(clearedProfile.DateOfBirth);

        // Verify GET after clear
        var getClearedResp = await client.GetAsync("/api/account/profile");
        var getClearedProfile = await getClearedResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(getClearedProfile);
        Assert.Null(getClearedProfile.Gender);
        Assert.Null(getClearedProfile.DateOfBirth);
    }

    // =========================================================================
    // 7. CORRUPTED BANK CIPHERTEXT & EMPTY BANK HANDLED GRACEFULLY
    // =========================================================================
    [Fact]
    public async Task CorruptedBankCiphertext_ReturnsNeedsUpdate_AndEmptyBankIsNull()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);

        // 1. Initial user has no bank -> Bank is null
        var noBankResp = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, noBankResp.StatusCode);
        var noBankProfile = await noBankResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(noBankProfile);
        Assert.Null(noBankProfile.Bank);

        // 2. Corrupt bank ciphertext in DB
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var profile = new UserProfile
            {
                UserId = user.Id,
                Address = "Corrupt Bank Addr",
                BankName = "Vietcombank",
                AccountHolder = "CORRUPT HOLDER",
                AccountEncrypted = "definitely-invalid-corrupted-ciphertext-12345",
                AccountLast4 = "1234",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);
            await db.SaveChangesAsync();
        }

        // 3. GET /api/account/profile returns 200 (NOT 500) with NEEDS_UPDATE and null number
        var corruptResp = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, corruptResp.StatusCode);
        var corruptProfile = await corruptResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(corruptProfile?.Bank);
        Assert.Equal("Vietcombank", corruptProfile.Bank.BankName);
        Assert.Equal("CORRUPT HOLDER", corruptProfile.Bank.AccountHolder);
        Assert.Equal("1234", corruptProfile.Bank.AccountLast4);
        Assert.Null(corruptProfile.Bank.AccountNumber); // null number
        Assert.Equal("NEEDS_UPDATE", corruptProfile.Bank.Status); // status NEEDS_UPDATE

        // 4. PUT replaces corrupted bank with valid bank
        var newBankReq = new UpdateUserProfileRequest(
            FullName: user.FullName,
            Phone: user.Phone,
            Address: "Repaired Bank Addr",
            Bank: new PersonalBankInput("Agribank", "REPAIRED HOLDER", "1234567890"));

        var repairResp = await client.PutAsJsonAsync("/api/account/profile", newBankReq);
        Assert.Equal(HttpStatusCode.OK, repairResp.StatusCode);
        var repairedProfile = await repairResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(repairedProfile?.Bank);
        Assert.Equal("Agribank", repairedProfile.Bank.BankName);
        Assert.Equal("REPAIRED HOLDER", repairedProfile.Bank.AccountHolder);
        Assert.Equal("7890", repairedProfile.Bank.AccountLast4);

        // GET confirms repair and full decrypted bank
        var getRepairedResp = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, getRepairedResp.StatusCode);
        var getRepairedProfile = await getRepairedResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(getRepairedProfile?.Bank);
        Assert.Equal("1234567890", getRepairedProfile.Bank.AccountNumber);
        Assert.Equal("READY", getRepairedProfile.Bank.Status);
    }

    // =========================================================================
    // 8. EMAIL FALLBACK & PHONE-ONLY ACCOUNT MANUAL OTP CONTACT VERIFICATION
    // =========================================================================
    [Fact]
    public async Task EmailFallback_And_PhoneOnlyUser_ManualOtpVerification()
    {
        // 1. Password/Google account with login email: contactEmail defaults to loginEmail
        var (emailUser, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (admin, _) = await CreateIsolatedUserAsync("ADMIN");
        using var adminClient = CreateAuthenticatedClient(admin);
        using (var emailClient = CreateAuthenticatedClient(emailUser))
        {
            var profResp = await emailClient.GetAsync("/api/account/profile");
            Assert.Equal(HttpStatusCode.OK, profResp.StatusCode);
            var prof = await profResp.Content.ReadFromJsonAsync<UserProfileResponse>();
            Assert.NotNull(prof);
            Assert.Equal(emailUser.Email, prof.LoginEmail);
            Assert.Equal(emailUser.Email, prof.ContactEmail); // Defaults to login email
            Assert.True(prof.ContactEmailIsDefault);
            Assert.False(prof.ContactEmailVerified);
            var adminResponse = await adminClient.GetAsync($"/api/admin/accounts/{emailUser.Id}/profile");
            Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
            var adminProfile = await adminResponse.Content.ReadFromJsonAsync<AdminAccountProfileResponse>();
            Assert.Equal(emailUser.Email, adminProfile!.ContactEmail);
        }

        // 2. Phone-only account (Email is null in DB): no placeholder generated email
        User phoneUser;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            phoneUser = new User
            {
                Id = Guid.NewGuid(),
                Email = null, // Phone-only user
                Phone = "09" + Random.Shared.Next(10000000, 99999999),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
                FullName = "Phone Only User",
                Role = "CUSTOMER",
                Status = "ACTIVE",
                LoyaltyPoints = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Users.Add(phoneUser);
            await db.SaveChangesAsync();
        }

        using var phoneClient = CreateAuthenticatedClient(phoneUser);
        var phoneProfResp = await phoneClient.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, phoneProfResp.StatusCode);
        var phoneProf = await phoneProfResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(phoneProf);
        Assert.Null(phoneProf.LoginEmail);
        Assert.Null(phoneProf.ContactEmail); // No placeholder generated email!
        Assert.True(phoneProf.ContactEmailIsDefault);
        Assert.False(phoneProf.ContactEmailVerified);
        var adminPhoneBefore = await adminClient.GetFromJsonAsync<AdminAccountProfileResponse>($"/api/admin/accounts/{phoneUser.Id}/profile");
        Assert.Null(adminPhoneBefore!.ContactEmail);

        // 3. Phone-only user manually adds contact email via OTP challenge
        var customContact = $"custom-phone-{Guid.NewGuid():N}@example.test";
        var challengeResp = await phoneClient.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(customContact));
        Assert.Equal(HttpStatusCode.OK, challengeResp.StatusCode);
        var challenge = await challengeResp.Content.ReadFromJsonAsync<ContactEmailChallengeResponse>();
        Assert.NotNull(challenge);

        // Extract code from fakeEmailSender
        var code = factory.EmailSender.ExtractLastCode(customContact);
        Assert.NotNull(code);

        // Verify OTP
        var verifyResp = await phoneClient.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, code));
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        var verifiedProf = await verifyResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(verifiedProf);
        Assert.Null(verifiedProf.LoginEmail); // Login email remains null!
        Assert.Equal(customContact, verifiedProf.ContactEmail);
        Assert.False(verifiedProf.ContactEmailIsDefault);
        Assert.True(verifiedProf.ContactEmailVerified);
        var adminPhoneAfter = await adminClient.GetFromJsonAsync<AdminAccountProfileResponse>($"/api/admin/accounts/{phoneUser.Id}/profile");
        Assert.Equal(customContact, adminPhoneAfter!.ContactEmail);
        Assert.True(adminPhoneAfter.ContactEmailVerified);

        // Verify DB state: user.Email is still null, profile.contact_email is verified
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
            var dbUser = await db.Users.Include(u => u.Profile).SingleAsync(u => u.Id == phoneUser.Id);
            Assert.Null(dbUser.Email); // NEVER change login email
            Assert.Equal(customContact, dbUser.Profile?.ContactEmail);
            Assert.NotNull(dbUser.Profile?.ContactEmailVerifiedAt);
        }
    }

    // =========================================================================
    // 9. PERSONAL PROFILE MUTATIONS DO NOT TOUCH KIOSK COORDS OR SELLER BANK
    // =========================================================================
    [Fact]
    public async Task PersonalProfile_Editing_NeverTouchesKioskCoords_OrSellerBank_OrWithdrawalSnapshots()
    {
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        var protection = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var shopPlainAccount = "098765432100";
        var encShopBank = protection.CreateProtector("bank-account-v1").Protect(shopPlainAccount);

        Guid kioskId = Guid.NewGuid();
        Guid withdrawalId = Guid.NewGuid();
        var initialLat = 10.7769;
        var initialLng = 106.7009;
        var initialKioskAddress = "Kiosk 101, Saigon Centre";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

            // Setup seller shop bank
            var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
            dbSeller.BankName = "Sacombank Shop";
            dbSeller.BankHolder = "CONG TY FLORA SHOP";
            dbSeller.BankAccountNoEnc = encShopBank;

            // Setup kiosk with coordinates
            var kiosk = new Kiosk
            {
                Id = kioskId,
                SellerId = seller.Id,
                Code = "K-" + Random.Shared.Next(1000, 9999),
                Name = "Saigon Kiosk",
                Address = initialKioskAddress,
                Region = "HCM",
                HardwareId = "HW-" + Guid.NewGuid().ToString("N")[..8],
                MqttClientId = "MQTT-" + Guid.NewGuid().ToString("N")[..8],
                Status = "OFFLINE",
                Latitude = initialLat,
                Longitude = initialLng,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.Kiosks.Add(kiosk);

            // Setup past withdrawal request with bank snapshot
            var withdrawal = new WithdrawalRequest
            {
                Id = withdrawalId,
                SellerId = seller.Id,
                Amount = 5000000,
                Status = "PENDING",
                BankName = "Sacombank Shop",
                BankHolder = "CONG TY FLORA SHOP",
                BankAccountNoEnc = encShopBank,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.WithdrawalRequests.Add(withdrawal);

            await db.SaveChangesAsync();
        }

        // Seller mutates personal profile: changes personal address and personal bank
        using var client = CreateAuthenticatedClient(sellerUser);
        var personalBankRaw = "01122334455";
        var putReq = new UpdateUserProfileRequest(
            FullName: "Seller Owner Mutated",
            Phone: sellerUser.Phone,
            Address: "New Private Residential Address, District 7",
            Bank: new PersonalBankInput("Techcombank Personal", "SELLER OWNER", personalBankRaw));

        var putResp = await client.PutAsJsonAsync("/api/account/profile", putReq);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        // Verify seller shop bank, kiosk coords, and past withdrawal request remain completely untouched!
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

            // 1. Seller shop bank untouched
            var dbSeller = await db.Sellers.SingleAsync(s => s.Id == seller.Id);
            Assert.Equal("Sacombank Shop", dbSeller.BankName);
            Assert.Equal("CONG TY FLORA SHOP", dbSeller.BankHolder);
            Assert.Equal(encShopBank, dbSeller.BankAccountNoEnc);

            // 2. Kiosk coordinates and address untouched
            var dbKiosk = await db.Kiosks.SingleAsync(k => k.Id == kioskId);
            Assert.Equal(initialLat, dbKiosk.Latitude);
            Assert.Equal(initialLng, dbKiosk.Longitude);
            Assert.Equal(initialKioskAddress, dbKiosk.Address);

            // 3. Withdrawal request snapshot untouched
            var dbWithdrawal = await db.WithdrawalRequests.SingleAsync(w => w.Id == withdrawalId);
            Assert.Equal("Sacombank Shop", dbWithdrawal.BankName);
            Assert.Equal("CONG TY FLORA SHOP", dbWithdrawal.BankHolder);
            Assert.Equal(encShopBank, dbWithdrawal.BankAccountNoEnc);
        }
    }
}
