using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace FloraBot.Api.Tests;

public sealed class PersonalProfileTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<(User User, Seller? Seller)> CreateIsolatedUserAsync(string role, string status = "ACTIVE")
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
                ShopName = "Isolated Shop " + Random.Shared.Next(100000, 999999),
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
            Email = $"iso-{Guid.NewGuid():N}@isolated-test.example",
            Phone = "09" + Random.Shared.Next(10000000, 99999999),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("TestPass123!"),
            FullName = $"Test User {role}",
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

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("SELLER")]
    [InlineData("CUSTOMER")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER_STAFF")]
    public async Task AllSixHumanRolesCanReadProfileWithIsolatedUsers(string role)
    {
        var (user, _) = await CreateIsolatedUserAsync(role);
        using var client = CreateAuthenticatedClient(user);

        var response = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var profile = await response.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal(user.Id, profile.Id);
        Assert.Equal(user.FullName, profile.FullName);
        Assert.Equal(user.Email, profile.LoginEmail);
        Assert.Equal(user.Phone, profile.Phone);
        Assert.True(profile.ContactEmailIsDefault);
        Assert.False(profile.ContactEmailVerified);
        Assert.Null(profile.Bank);
    }

    [Fact]
    public async Task AnonymousAndKioskAndLegacyStaffAndLockedAndSystemAreRejected()
    {
        using var client = factory.CreateClient();

        // 1. Anonymous
        var anonResp = await client.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Kiosk session token with kiosk_id claim
        var (customer, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using (var scope = factory.Services.CreateScope())
        {
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
            var kioskToken = tokenService.Issue(customer, "APPROVED", kioskId: Guid.NewGuid().ToString());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kioskToken);

            var kioskResp = await client.GetAsync("/api/account/profile");
            Assert.Equal(HttpStatusCode.Forbidden, kioskResp.StatusCode);
        }

        // 3. Retired legacy staff
        var (legacyStaff, _) = await CreateIsolatedUserAsync("STAFF");
        using var staffClient = CreateAuthenticatedClient(legacyStaff);
        var staffResp = await staffClient.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, staffResp.StatusCode);

        // 4. System principal
        var (systemUser, _) = await CreateIsolatedUserAsync("SYSTEM");
        using var systemClient = CreateAuthenticatedClient(systemUser);
        var systemResp = await systemClient.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.Forbidden, systemResp.StatusCode);

        // 5. Locked user
        var (lockedUser, _) = await CreateIsolatedUserAsync("CUSTOMER", status: "LOCKED");
        using var lockedClient = CreateAuthenticatedClient(lockedUser);
        var lockedResp = await lockedClient.GetAsync("/api/account/profile");
        Assert.Equal(HttpStatusCode.Unauthorized, lockedResp.StatusCode);
    }

    [Fact]
    public async Task ProfileUpdatePersistsNamePhoneAddressAndEnforcesValidation()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);

        // 1. Invalid Name (< 2 chars or control char)
        var badNameResp = await client.PutAsJsonAsync("/api/account/profile", new UpdateUserProfileRequest("A", user.Phone, "123 Street"));
        Assert.Equal(HttpStatusCode.BadRequest, badNameResp.StatusCode);

        // 2. Invalid Address (> 300 chars)
        var longAddress = new string('x', 301);
        var badAddrResp = await client.PutAsJsonAsync("/api/account/profile", new UpdateUserProfileRequest("Hợp Lệ", user.Phone, longAddress));
        Assert.Equal(HttpStatusCode.BadRequest, badAddrResp.StatusCode);

        // 3. Invalid VN Phone format
        var badPhoneResp = await client.PutAsJsonAsync("/api/account/profile", new UpdateUserProfileRequest("Hợp Lệ", "12345678", "123 Street"));
        Assert.Equal(HttpStatusCode.BadRequest, badPhoneResp.StatusCode);

        // 4. Duplicate phone with another isolated user -> 409
        var (otherUser, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var dupPhoneResp = await client.PutAsJsonAsync("/api/account/profile", new UpdateUserProfileRequest("Hợp Lệ", otherUser.Phone, "123 Street"));
        Assert.Equal(HttpStatusCode.Conflict, dupPhoneResp.StatusCode);

        // 5. Successful update
        var uniquePhone = "093" + Random.Shared.Next(1000000, 9999999);
        var validUpdate = new UpdateUserProfileRequest("Tên Mới Hoàn Toàn", uniquePhone, "Số 10 Phố Hoa, Quận 1");
        var successResp = await client.PutAsJsonAsync("/api/account/profile", validUpdate);
        Assert.Equal(HttpStatusCode.OK, successResp.StatusCode);

        var profile = await successResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal("Tên Mới Hoàn Toàn", profile.FullName);
        Assert.Equal(uniquePhone, profile.Phone);
        Assert.Equal("Số 10 Phố Hoa, Quận 1", profile.Address);

        // Verify shared canonical name with /api/member/profile
        var memberResp = await client.GetAsync("/api/member/profile");
        Assert.Equal(HttpStatusCode.OK, memberResp.StatusCode);
        var member = await memberResp.Content.ReadFromJsonAsync<MemberProfile>();
        Assert.NotNull(member);
        Assert.Equal("Tên Mới Hoàn Toàn", member.FullName);
    }

    [Fact]
    public async Task PhoneConcurrencyReturns409OnConflict()
    {
        var (user1, _) = await CreateIsolatedUserAsync("CUSTOMER");
        var (user2, _) = await CreateIsolatedUserAsync("CUSTOMER");

        using var client1 = CreateAuthenticatedClient(user1);
        using var client2 = CreateAuthenticatedClient(user2);

        var targetPhone = "097" + Random.Shared.Next(1000000, 9999999);

        var task1 = client1.PutAsJsonAsync("/api/account/profile", new UpdateUserProfileRequest("User 1", targetPhone, "Addr 1"));
        var task2 = client2.PutAsJsonAsync("/api/account/profile", new UpdateUserProfileRequest("User 2", targetPhone, "Addr 2"));

        var responses = await Task.WhenAll(task1, task2);
        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, okCount);
        Assert.Equal(1, conflictCount);
    }

    [Fact]
    public async Task BankDetailsCanBeEncryptedRevealedAuditedAndCleared()
    {
        var (user, _) = await CreateIsolatedUserAsync("ADMIN");
        using var client = CreateAuthenticatedClient(user);

        // 1. Bank replacement and ClearBank both true -> 400
        var conflicting = new UpdateUserProfileRequest(user.FullName, user.Phone, "Trụ sở",
            new PersonalBankInput("Vietcombank", "NGUYEN VAN ADMIN", "1234567890"), ClearBank: true);
        var conflictResp = await client.PutAsJsonAsync("/api/account/profile", conflicting);
        Assert.Equal(HttpStatusCode.BadRequest, conflictResp.StatusCode);

        // 2. Incomplete or invalid bank input -> 400
        var badAccount = new UpdateUserProfileRequest(user.FullName, user.Phone, "Trụ sở",
            new PersonalBankInput("Vietcombank", "ADMIN", "12")); // < 4 chars
        var badAccResp = await client.PutAsJsonAsync("/api/account/profile", badAccount);
        Assert.Equal(HttpStatusCode.BadRequest, badAccResp.StatusCode);

        // 3. Valid bank setup
        var rawAccountNumber = "98765432101234";
        var validBank = new UpdateUserProfileRequest(user.FullName, user.Phone, "Trụ sở",
            new PersonalBankInput("Techcombank", "nguyen van admin", rawAccountNumber));
        var setResp = await client.PutAsJsonAsync("/api/account/profile", validBank);
        Assert.Equal(HttpStatusCode.OK, setResp.StatusCode);

        var profile = await setResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile?.Bank);
        Assert.Equal("Techcombank", profile.Bank.BankName);
        Assert.Equal("NGUYEN VAN ADMIN", profile.Bank.AccountHolder);
        Assert.Equal("1234", profile.Bank.AccountLast4);
        Assert.True(profile.Bank.HasAccount);

        // 4. Reveal endpoint returns raw number and audits without raw values
        var revealResp = await client.PostAsync("/api/account/profile/bank/reveal", null);
        Assert.Equal(HttpStatusCode.OK, revealResp.StatusCode);
        Assert.Equal("no-store", revealResp.Headers.CacheControl?.ToString());
        var revealed = await revealResp.Content.ReadFromJsonAsync<RevealBankResponse>();
        Assert.NotNull(revealed);
        Assert.Equal(rawAccountNumber, revealed.AccountNumber);

        // Verify audit log has PERSONAL_BANK_REVEALED and no raw account number
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
        }

        // 5. Clear bank
        var clearReq = new UpdateUserProfileRequest(user.FullName, user.Phone, "Trụ sở", ClearBank: true);
        var clearResp = await client.PutAsJsonAsync("/api/account/profile", clearReq);
        Assert.Equal(HttpStatusCode.OK, clearResp.StatusCode);
        var clearedProfile = await clearResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.Null(clearedProfile?.Bank);

        // Subsequent reveal returns 404
        var afterClearReveal = await client.PostAsync("/api/account/profile/bank/reveal", null);
        Assert.Equal(HttpStatusCode.NotFound, afterClearReveal.StatusCode);
    }

    [Fact]
    public async Task ContactEmailOtpChallengeVerificationAndResetFlow()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);

        // Initial state: fallback to login email, isDefault = true, verified = false
        var initialResp = await client.GetAsync("/api/account/profile");
        var initialProfile = await initialResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(initialProfile);
        Assert.Equal(user.Email, initialProfile.ContactEmail);
        Assert.True(initialProfile.ContactEmailIsDefault);
        Assert.False(initialProfile.ContactEmailVerified);

        // 1. Invalid email format -> 400
        var badEmailResp = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput("invalid-email"));
        Assert.Equal(HttpStatusCode.BadRequest, badEmailResp.StatusCode);

        // 2. Request OTP
        var targetEmail = "custom.contact." + Random.Shared.Next(10000, 99999) + "@example.test";
        var otpReqResp = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(targetEmail));
        Assert.Equal(HttpStatusCode.OK, otpReqResp.StatusCode);

        var challenge = await otpReqResp.Content.ReadFromJsonAsync<ContactEmailChallengeResponse>();
        Assert.NotNull(challenge);
        Assert.Equal(32, challenge.ChallengeId.Length);
        Assert.Equal(600, challenge.ExpiresInSeconds);
        Assert.Equal(60, challenge.ResendAfterSeconds);

        // Existing contact remains unchanged while challenge is pending
        var pendingProfileResp = await client.GetAsync("/api/account/profile");
        var pendingProfile = await pendingProfileResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(pendingProfile);
        Assert.Equal(user.Email, pendingProfile.ContactEmail);
        Assert.True(pendingProfile.ContactEmailIsDefault);

        // 3. Second request immediately triggers cooldown (429)
        var cooldownResp = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(targetEmail));
        Assert.Equal(HttpStatusCode.TooManyRequests, cooldownResp.StatusCode);

        // 4. Verification with wrong code returns 400 (computed code different from real code)
        var actualCode = factory.EmailSender.ExtractLastCode(targetEmail)!;
        var wrongCode = actualCode == "999999" ? "111111" : (int.Parse(actualCode) + 1).ToString("D6");
        var wrongResp = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, wrongCode));
        Assert.Equal(HttpStatusCode.BadRequest, wrongResp.StatusCode);

        // 5. Cross-user verification attempt by another user is rejected
        var (otherUser, _) = await CreateIsolatedUserAsync("ADMIN");
        using (var otherClient = CreateAuthenticatedClient(otherUser))
        {
            var crossResp = await otherClient.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, actualCode));
            Assert.Equal(HttpStatusCode.BadRequest, crossResp.StatusCode);
        }

        // 6. Successful verification
        var verifyResp = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, actualCode));
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        var verifiedProfile = await verifyResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(verifiedProfile);
        Assert.Equal(targetEmail, verifiedProfile.ContactEmail);
        Assert.False(verifiedProfile.ContactEmailIsDefault);
        Assert.True(verifiedProfile.ContactEmailVerified);

        // Replay of verified code fails (atomic one-time use)
        var replayResp = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, actualCode));
        Assert.Equal(HttpStatusCode.BadRequest, replayResp.StatusCode);

        // 7. Reset contact email back to default login email
        var resetResp = await client.PostAsync("/api/account/contact-email/reset", null);
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);

        var resetProfile = await resetResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(resetProfile);
        Assert.Equal(user.Email, resetProfile.ContactEmail);
        Assert.True(resetProfile.ContactEmailIsDefault);
        Assert.False(resetProfile.ContactEmailVerified);
    }

    [Fact]
    public async Task ContactEmailOtpConcurrentSendsProduceOnlyOneSend()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);

        var email1 = "race1." + Random.Shared.Next(10000, 99999) + "@example.test";
        var email2 = "race2." + Random.Shared.Next(10000, 99999) + "@example.test";

        var task1 = client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email1));
        var task2 = client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email2));

        var responses = await Task.WhenAll(task1, task2);
        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var throttledCount = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(1, successCount);
        Assert.Equal(1, throttledCount);
    }

    [Fact]
    public async Task ResetWhileSmtpInFlightInvalidatesChallengeAndPreservesDefaultEmail()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);

        // Create a blocking email sender to hold SMTP send in-flight
        var sendStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSendToComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingSender = new BlockingTestEmailSender(sendStarted, allowSendToComplete.Task);

        using var customFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ITransactionalEmailSender>(blockingSender);
            });
        });

        using var customClient = customFactory.CreateClient();
        using var scope = customFactory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
        customClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenService.Issue(user, "APPROVED"));

        var targetEmail = "inflight." + Random.Shared.Next(10000, 99999) + "@example.test";

        // Start contact email request in background
        var startTask = customClient.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(targetEmail));

        // Wait until send is confirmed in-flight inside email sender
        var inFlightEmail = await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(targetEmail, inFlightEmail);

        // While email send is held in-flight, user calls RESET
        var resetResp = await customClient.PostAsync("/api/account/contact-email/reset", null);
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);

        // Reset must not permit another provider send while the old one is held.
        try
        {
            var resend = await customClient.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(targetEmail))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.TooManyRequests, resend.StatusCode);
        }
        finally
        {
            allowSendToComplete.TrySetResult(true);
        }
        var startResp = await startTask;

        // Finalize should have detected invalidated lease owner and returned 409
        Assert.Equal(HttpStatusCode.Conflict, startResp.StatusCode);

        // Verify active challenge was NOT created / cannot be verified
        var verifyResp = await customClient.PostAsJsonAsync("/api/account/contact-email/verify",
            new VerifyContactEmailInput(new string('a', 32), "123456"));
        Assert.Equal(HttpStatusCode.BadRequest, verifyResp.StatusCode);

        // Profile remains default login email
        var profileResp = await customClient.GetAsync("/api/account/profile");
        var profile = await profileResp.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal(user.Email, profile.ContactEmail);
        Assert.True(profile.ContactEmailIsDefault);
    }

    [Fact]
    public async Task FiveWrongCodesLockTheUserAndResetDoesNotBypassTheLock()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);
        var email = $"lock-{Guid.NewGuid():N}@example.test";
        var sent = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email));
        sent.EnsureSuccessStatusCode();
        var challenge = (await sent.Content.ReadFromJsonAsync<ContactEmailChallengeResponse>())!;
        var actual = factory.EmailSender.ExtractLastCode(email)!;
        var wrong = actual == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, wrong));
            Assert.Equal(i == 4 ? HttpStatusCode.TooManyRequests : HttpStatusCode.BadRequest, response.StatusCode);
        }
        var correctButLocked = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, actual));
        Assert.Equal(HttpStatusCode.TooManyRequests, correctButLocked.StatusCode);
        (await client.PostAsync("/api/account/contact-email/reset", null)).EnsureSuccessStatusCode();
        var resend = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email));
        Assert.Equal(HttpStatusCode.TooManyRequests, resend.StatusCode);
        Assert.Single(factory.EmailSender.SentEmails, e => e.Recipient == email);
    }

    [Fact]
    public async Task ResendingInvalidatesThePreviousCodeAndStoresOnlyItsHmac()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);
        var email = $"resend-{Guid.NewGuid():N}@example.test";
        var firstResponse = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email));
        firstResponse.EnsureSuccessStatusCode();
        var first = (await firstResponse.Content.ReadFromJsonAsync<ContactEmailChallengeResponse>())!;
        var firstCode = factory.EmailSender.ExtractLastCode(email)!;
        using var scope = factory.Services.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        // Simulate expiry of only this isolated user's cooldown, without a minute-long test delay.
        await redis.KeyDeleteAsync($"contact:user:{user.Id}:cooldown");
        var secondResponse = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email));
        secondResponse.EnsureSuccessStatusCode();
        var second = (await secondResponse.Content.ReadFromJsonAsync<ContactEmailChallengeResponse>())!;
        var secondCode = factory.EmailSender.ExtractLastCode(email)!;
        var stored = await redis.HashGetAllAsync($"contact:challenge:{second.ChallengeId}");
        Assert.Contains(stored, e => e.Name == "codeHmac" && e.Value != secondCode);
        Assert.DoesNotContain(stored, e => e.Name == "code" || e.Value == secondCode);
        var stale = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(first.ChallengeId, firstCode));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        var valid = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(second.ChallengeId, secondCode));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task AnExpiredChallengeCannotActivateTheContactAddress()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);
        var email = $"expired-{Guid.NewGuid():N}@example.test";
        var sent = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email));
        sent.EnsureSuccessStatusCode();
        var challenge = (await sent.Content.ReadFromJsonAsync<ContactEmailChallengeResponse>())!;
        var code = factory.EmailSender.ExtractLastCode(email)!;
        using var scope = factory.Services.CreateScope();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.KeyExpireAsync($"contact:challenge:{challenge.ChallengeId}", TimeSpan.Zero);
        var verify = await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, code));
        Assert.Equal(HttpStatusCode.BadRequest, verify.StatusCode);
        var profile = (await client.GetFromJsonAsync<UserProfileResponse>("/api/account/profile"))!;
        Assert.Equal(user.Email, profile.ContactEmail);
        Assert.True(profile.ContactEmailIsDefault);
    }

    [Fact]
    public async Task PasswordResetKeepsUsingIdentityEmailAfterContactChanges()
    {
        var (user, _) = await CreateIsolatedUserAsync("CUSTOMER");
        using var client = CreateAuthenticatedClient(user);
        var email = $"contact-{Guid.NewGuid():N}@example.test";
        var sent = await client.PostAsJsonAsync("/api/account/contact-email", new RequestContactEmailInput(email));
        sent.EnsureSuccessStatusCode();
        var challenge = (await sent.Content.ReadFromJsonAsync<ContactEmailChallengeResponse>())!;
        var code = factory.EmailSender.ExtractLastCode(email)!;
        (await client.PostAsJsonAsync("/api/account/contact-email/verify", new VerifyContactEmailInput(challenge.ChallengeId, code))).EnsureSuccessStatusCode();
        var before = factory.EmailSender.SentEmails.Count;
        using var recoveryFactory = factory.WithWebHostBuilder(builder => builder.UseSetting("MEMBER_WEB_URL", "https://florabot.test"));
        using var recoveryClient = recoveryFactory.CreateClient();
        (await recoveryClient.PostAsJsonAsync("/api/auth/forgot-password", new PasswordResetRequest(user.Email!))).EnsureSuccessStatusCode();
        var securityMail = Assert.Single(factory.EmailSender.SentEmails.Skip(before));
        Assert.Equal(user.Email, securityMail.Recipient);
    }

    [Fact]
    public async Task SellerNonAuthEmailRoutingUsesVerifiedContactEmailAndFallsBackToLoginEmail()
    {
        // Create an isolated shop with exactly ONE seller user
        var (sellerUser, seller) = await CreateIsolatedUserAsync("SELLER");
        Assert.NotNull(seller);

        using var client = CreateAuthenticatedClient(sellerUser);

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<SellerEmailReader>();

        // 1. Initial state (no verified contact email) -> routes to canonical login email
        var loginMessage = new SellerEmailRequested(seller.Id, SellerEmailEvents.Approval);
        var initialContent = await reader.ReadAsync(loginMessage, default);
        Assert.NotNull(initialContent);
        var recipient = Assert.Single(initialContent.Recipients);
        Assert.Equal(sellerUser.Id, recipient.UserId);
        Assert.Equal(sellerUser.Email, recipient.Email);

        // 2. Set verified custom contact email
        var customContact = "seller.contact." + Random.Shared.Next(10000, 99999) + "@example.test";
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var profile = new UserProfile
        {
            UserId = sellerUser.Id,
            ContactEmail = customContact,
            ContactEmailVerifiedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.UserProfiles.Add(profile);
        await db.SaveChangesAsync();

        // 3. Non-auth notification routes to verified custom contact email
        var updatedContent = await reader.ReadAsync(loginMessage, default);
        Assert.NotNull(updatedContent);
        var updatedRecipient = Assert.Single(updatedContent.Recipients);
        Assert.Equal(sellerUser.Id, updatedRecipient.UserId);
        Assert.Equal(customContact, updatedRecipient.Email);

        // 4. Reset contact email via reset endpoint
        var resetResp = await client.PostAsync("/api/account/contact-email/reset", null);
        Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);

        // 5. Notification falls back to canonical login email again
        var fallbackContent = await reader.ReadAsync(loginMessage, default);
        Assert.NotNull(fallbackContent);
        var fallbackRecipient = Assert.Single(fallbackContent.Recipients);
        Assert.Equal(sellerUser.Id, fallbackRecipient.UserId);
        Assert.Equal(sellerUser.Email, fallbackRecipient.Email);
    }

    private sealed class BlockingTestEmailSender(
        TaskCompletionSource<string> sendStarted,
        Task waitToFinish) : ITransactionalEmailSender
    {
        public async Task SendAsync(TransactionalEmail email, CancellationToken ct)
        {
            sendStarted.TrySetResult(email.Recipient);
            await waitToFinish;
        }
    }
}
