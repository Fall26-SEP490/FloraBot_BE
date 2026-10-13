using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Notify;
using Google.Apis.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using StackExchange.Redis;
using Xunit;

namespace FloraBot.Api.Tests;

public sealed class AuthRealProviderTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AuthConfigReturnsPublicClientConfiguration()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/auth/config");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var config = await response.Content.ReadFromJsonAsync<AuthConfigResponse>();
        Assert.NotNull(config);
        Assert.Equal("test-google-client-id", config.GoogleClientId);
        Assert.True(config.GoogleEnabled);
        Assert.True(config.RegistrationEmailEnabled);
    }

    [Fact]
    public async Task RealInvalidAndMagicMockTokensAreRejectedEvenInDevelopmentAndWithLegacyFlags()
    {
        using var client = factory.CreateClient();

        // Magic mock token
        var mockResponse = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("mock-google-token:user@test.com:User"));
        Assert.Equal(HttpStatusCode.Unauthorized, mockResponse.StatusCode);

        // Untrusted/arbitrary token
        var invalidResponse = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("any-invalid-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);
    }

    [Theory]
    [InlineData("wrong-audience", true, "sub-123", "email@test.com", HttpStatusCode.Unauthorized)]
    [InlineData("test-google-client-id", false, "sub-123", "email@test.com", HttpStatusCode.BadRequest)]
    [InlineData("test-google-client-id", true, "", "email@test.com", HttpStatusCode.BadRequest)]
    [InlineData("test-google-client-id", true, "sub-123", "", HttpStatusCode.BadRequest)]
    public async Task GoogleValidationSeamEnforcesAudienceEmailVerifiedAndSub(
        string tokenAudience, bool emailVerified, string sub, string email, HttpStatusCode expectedStatus)
    {
        try
        {
            factory.GoogleValidator.Handler = (token, aud) =>
            {
                if (tokenAudience != aud) throw new InvalidJwtException("Wrong audience");
                return new GoogleJsonWebSignature.Payload
                {
                    Audience = tokenAudience,
                    EmailVerified = emailVerified,
                    Subject = sub,
                    Email = email
                };
            };

            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("test-token"));
            Assert.Equal(expectedStatus, response.StatusCode);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Fact]
    public async Task GoogleCustomerNewRegistrationAndSubsequentLoginPreservesCustomerRole()
    {
        var email = $"google-cust-{Guid.NewGuid():N}@gmail.com";
        var sub = $"google-sub-{Guid.NewGuid():N}";

        try
        {
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = sub,
                Email = email,
                Name = "Google Customer"
            };

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

            // First login: registers new CUSTOMER
            var firstResp = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("google-verified-token-1"));
            Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
            var session = await firstResp.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.NotNull(session);
            Assert.Equal("CUSTOMER", session.Role);
            Assert.Equal("Google Customer", session.Name);

            var cookies = firstResp.Headers.GetValues("Set-Cookie").ToList();
            Assert.Contains(cookies, c => c.StartsWith("florabot_access=", StringComparison.Ordinal));
            Assert.Contains(cookies, c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal));

            // Second login: finds existing external identity linkage
            var secondResp = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("google-verified-token-2"));
            Assert.Equal(HttpStatusCode.OK, secondResp.StatusCode);
            var session2 = await secondResp.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal(session.Id, session2!.Id);
            Assert.Equal("CUSTOMER", session2.Role);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Fact]
    public async Task GoogleAuthPreservesExistingSellerRoleWhenLinked()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var sellerEmail = $"seller-link-{Guid.NewGuid():N}@example.test";
        var sub = $"google-sub-{Guid.NewGuid():N}";
        var sellerUserId = Guid.CreateVersion7();
        var shopId = Guid.Parse("20000000-0000-0000-0000-000000000001");

        await using var cmd = source.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id) " +
            "VALUES(@id, @email, '$2b$12$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 'Seller Shop Owner', 'SELLER', 'ACTIVE', @sellerId);" +
            "INSERT INTO identity.external_identities(user_id, provider, subject, email) VALUES(@id, 'google', @sub, @email);");
        cmd.Parameters.AddWithValue("id", sellerUserId);
        cmd.Parameters.AddWithValue("email", sellerEmail);
        cmd.Parameters.AddWithValue("sellerId", shopId);
        cmd.Parameters.AddWithValue("sub", sub);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = sub,
                Email = sellerEmail,
                Name = "Seller Shop Owner"
            };

            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("seller-token"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.NotNull(session);
            Assert.Equal("SELLER", session.Role);
            Assert.Equal(sellerUserId, session.Id);
            Assert.Equal(shopId, session.SellerId);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER_STAFF")]
    [InlineData("STAFF")]
    public async Task GoogleAuthForbidsPrivilegedAndStaffAccounts(string privilegedRole)
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var privEmail = $"priv-{Guid.NewGuid():N}@example.test";
        var sub = $"google-sub-{Guid.NewGuid():N}";
        var privUserId = Guid.CreateVersion7();
        var sellerId = privilegedRole == "SELLER_STAFF" ? (object)Guid.Parse("20000000-0000-0000-0000-000000000001") : DBNull.Value;

        await using var cmd = source.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id) " +
            "VALUES(@id, @email, '$2b$12$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 'Priv User', @role, 'ACTIVE', @sellerId);" +
            "INSERT INTO identity.external_identities(user_id, provider, subject, email) VALUES(@id, 'google', @sub, @email);");
        cmd.Parameters.AddWithValue("id", privUserId);
        cmd.Parameters.AddWithValue("email", privEmail);
        cmd.Parameters.AddWithValue("role", privilegedRole);
        cmd.Parameters.AddWithValue("sellerId", sellerId);
        cmd.Parameters.AddWithValue("sub", sub);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = sub,
                Email = privEmail
            };

            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("priv-token"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Fact]
    public async Task GoogleAuthRejectsLockedOrDisabledUser()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var lockedEmail = $"locked-{Guid.NewGuid():N}@example.test";
        var sub = $"google-sub-{Guid.NewGuid():N}";
        var lockedUserId = Guid.CreateVersion7();

        await using var cmd = source.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, role, status) " +
            "VALUES(@id, @email, NULL, 'Locked User', 'CUSTOMER', 'LOCKED');" +
            "INSERT INTO identity.external_identities(user_id, provider, subject, email) VALUES(@id, 'google', @sub, @email);");
        cmd.Parameters.AddWithValue("id", lockedUserId);
        cmd.Parameters.AddWithValue("email", lockedEmail);
        cmd.Parameters.AddWithValue("sub", sub);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = sub,
                Email = lockedEmail
            };

            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("locked-token"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Fact]
    public async Task GoogleAuthReturnsHelpfulConflictOnUnlinkedExistingEmailCollision()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var collisionEmail = $"collision-{Guid.NewGuid():N}@example.test";
        var existingUserId = Guid.CreateVersion7();

        await using var cmd = source.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, role, status) " +
            "VALUES(@id, @email, '$2b$12$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 'Existing Password User', 'CUSTOMER', 'ACTIVE');");
        cmd.Parameters.AddWithValue("id", existingUserId);
        cmd.Parameters.AddWithValue("email", collisionEmail);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = $"google-sub-{Guid.NewGuid():N}",
                Email = collisionEmail
            };

            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("collision-token"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var detail = await response.Content.ReadAsStringAsync();
            Assert.Contains("liên kết tài khoản Google", detail);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Fact]
    public async Task MemberCanExplicitlyLinkGoogleAccountWhenEmailMatches()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var memberEmail = $"member-link-{Guid.NewGuid():N}@example.test";
        var sub = $"google-sub-{Guid.NewGuid():N}";
        var memberId = Guid.CreateVersion7();

        await using var cmd = source.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, role, status) " +
            "VALUES(@id, @email, '$2b$12$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 'Member Linker', 'CUSTOMER', 'ACTIVE');");
        cmd.Parameters.AddWithValue("id", memberId);
        cmd.Parameters.AddWithValue("email", memberEmail);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();
            var memberToken = factory.Token("CUSTOMER", userId: memberId);
            client.DefaultRequestHeaders.Authorization = new("Bearer", memberToken);

            // 1. Linking with mismatched email returns 400
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = sub,
                Email = "different-email@example.test"
            };
            var mismatchResp = await client.PostAsJsonAsync("/api/member/google/link", new GoogleAuthRequest("google-linking-token"));
            Assert.Equal(HttpStatusCode.BadRequest, mismatchResp.StatusCode);

            // 2. Linking with matching email succeeds and does not change role
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = sub,
                Email = memberEmail
            };
            var linkResp = await client.PostAsJsonAsync("/api/member/google/link", new GoogleAuthRequest("google-linking-token"));
            Assert.Equal(HttpStatusCode.OK, linkResp.StatusCode);

            await using var check = source.CreateCommand("SELECT count(*) FROM identity.external_identities WHERE user_id=@id AND subject=@sub");
            check.Parameters.AddWithValue("id", memberId);
            check.Parameters.AddWithValue("sub", sub);
            Assert.Equal(1L, (long)(await check.ExecuteScalarAsync())!);

            // 3. Now logging in via Google Auth works for this user
            client.DefaultRequestHeaders.Authorization = null;
            var loginResp = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("google-linking-token"));
            Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
            var session = await loginResp.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal(memberId, session!.Id);
            Assert.Equal("CUSTOMER", session.Role);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Fact]
    public async Task RegistrationOtpLifecycleHappyPathAndAtomicConsumption()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var email = $"otp-happy-{Guid.NewGuid():N}@example.test";
        const string password = "StrongPassword1234!";

        // 1. Register starts verification, no user created yet
        var regResp = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("OTP User", email, password));
        Assert.Equal(HttpStatusCode.OK, regResp.StatusCode);

        var challenge = await regResp.Content.ReadFromJsonAsync<RegistrationChallengeResponse>();
        Assert.NotNull(challenge);
        Assert.False(string.IsNullOrWhiteSpace(challenge.ChallengeId));
        Assert.Equal(600, challenge.ExpiresInSeconds);
        Assert.Equal(60, challenge.ResendAfterSeconds);

        // No cookies issued yet
        Assert.DoesNotContain(regResp.Headers, h => h.Key == "Set-Cookie");

        // Email was captured by test sender
        var sentEmail = factory.EmailSender.SentEmails.Last();
        Assert.Equal(email, sentEmail.Recipient);
        var code = factory.EmailSender.ExtractLastCode();
        Assert.NotNull(code);
        Assert.Equal(6, code.Length);

        // User is not yet in database
        using (var scope = factory.Services.CreateScope())
        {
            var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var chk = source.CreateCommand("SELECT count(*) FROM identity.users WHERE email=@email");
            chk.Parameters.AddWithValue("email", email);
            Assert.Equal(0L, (long)(await chk.ExecuteScalarAsync())!);
        }

        // 2. Verify with valid code creates CUSTOMER and issues cookies
        var verifyResp = await client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(challenge.ChallengeId, code));
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        var session = await verifyResp.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(session);
        Assert.Equal("CUSTOMER", session.Role);
        Assert.Equal("OTP User", session.Name);

        var cookies = verifyResp.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.StartsWith("florabot_access=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal));

        // User now exists in DB and can log in with password
        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);

        // 3. Replay: cannot consume challenge a second time
        var replayResp = await client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(challenge.ChallengeId, code));
        Assert.Equal(HttpStatusCode.BadRequest, replayResp.StatusCode);
    }

    [Fact]
    public async Task RegistrationOtpWrongCodeCooldownResendInvalidatesEarlierAndAttemptLock()
    {
        using var client = factory.CreateClient();

        var email = $"otp-lock-{Guid.NewGuid():N}@example.test";
        const string password = "StrongPassword1234!";

        // 1. Initial register
        var reg1 = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Attempt User", email, password));
        Assert.Equal(HttpStatusCode.OK, reg1.StatusCode);
        var chal1 = await reg1.Content.ReadFromJsonAsync<RegistrationChallengeResponse>();
        var code1 = factory.EmailSender.ExtractLastCode()!;

        // 2. Immediate resend is rejected by 60s cooldown
        var cooldownResp = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Attempt User", email, password));
        Assert.Equal(HttpStatusCode.TooManyRequests, cooldownResp.StatusCode);
        Assert.True(cooldownResp.Headers.Contains("Retry-After"));

        // 3. Resend after cooldown invalidates earlier code: simulate cooldown expiry in Valkey
        var redis = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var emailHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(email)));
        await redis.KeyDeleteAsync($"reg:email:{emailHash}:cooldown");

        var reg2 = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Attempt User", email, password));
        Assert.Equal(HttpStatusCode.OK, reg2.StatusCode);
        var chal2 = await reg2.Content.ReadFromJsonAsync<RegistrationChallengeResponse>();
        var code2 = factory.EmailSender.ExtractLastCode()!;

        // Verifying old challenge 1 now fails because resend invalidated it
        var oldVerify = await client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(chal1!.ChallengeId, code1));
        Assert.Equal(HttpStatusCode.BadRequest, oldVerify.StatusCode);

        // 4. Failed attempts up to 5 locks the challenge
        for (var i = 1; i <= 4; i++)
        {
            var wrongResp = await client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(chal2!.ChallengeId, "000000"));
            Assert.Equal(HttpStatusCode.BadRequest, wrongResp.StatusCode);
        }

        // 5th failed attempt locks the account
        var fifthWrong = await client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(chal2!.ChallengeId, "000000"));
        Assert.Equal(HttpStatusCode.TooManyRequests, fifthWrong.StatusCode);

        // Any further verification attempts are locked
        var lockedVerify = await client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(chal2!.ChallengeId, code2));
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedVerify.StatusCode);

        // Even after deleting cooldown, new registration request is locked
        await redis.KeyDeleteAsync($"reg:email:{emailHash}:cooldown");
        var lockedReg = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Attempt User", email, password));
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedReg.StatusCode);
    }

    [Fact]
    public async Task RegistrationConcurrencyAllowsOnlyOneConsumer()
    {
        using var client = factory.CreateClient();

        var email = $"otp-race-{Guid.NewGuid():N}@example.test";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Race User", email, "Password12345!"));
        var chal = await reg.Content.ReadFromJsonAsync<RegistrationChallengeResponse>();
        var code = factory.EmailSender.ExtractLastCode()!;

        // Spawn 5 concurrent verify requests with the same challenge and code
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync("/api/auth/register/verify", new VerifyRegistrationRequest(chal!.ChallengeId, code)))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r.StatusCode == HttpStatusCode.OK);
        var failedCount = results.Count(r => r.StatusCode == HttpStatusCode.BadRequest);

        Assert.Equal(1, successCount);
        Assert.Equal(4, failedCount);
    }

    [Fact]
    public async Task DisabledEmailProviderOrSendFailureReturns503AndNoUser()
    {
        var email = $"disabled-email-{Guid.NewGuid():N}@example.test";

        // 1. Sender failure
        try
        {
            factory.EmailSender.ShouldFail = true;
            using var client = factory.CreateClient();

            var failResp = await client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Failing User", email, "Password12345!"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failResp.StatusCode);

            // No user was created in the database
            using var scope = factory.Services.CreateScope();
            var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
            await using var chk = source.CreateCommand("SELECT count(*) FROM identity.users WHERE email=@email");
            chk.Parameters.AddWithValue("email", email);
            Assert.Equal(0L, (long)(await chk.ExecuteScalarAsync())!);
        }
        finally
        {
            factory.EmailSender.ShouldFail = false;
        }

        // 2. Disabled email provider
        using var disabledApp = factory.WithWebHostBuilder(b => b.UseSetting("EMAIL_PROVIDER", "Disabled"));
        using var disabledClient = disabledApp.CreateClient();

        var disabledResp = await disabledClient.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Disabled User", email, "Password12345!"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledResp.StatusCode);
    }

    [Fact]
    public async Task RealProductionValidatorRejectsMockTokenInDevelopment()
    {
        using var realApp = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                var toRemove = services.Where(d => d.ServiceType == typeof(IGoogleIdentityValidator)).ToList();
                foreach (var r in toRemove) services.Remove(r);
                services.AddSingleton<IGoogleIdentityValidator, GoogleIdentityValidator>();
            });
        });
        using var client = realApp.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("mock-google-token:user@test.com:User"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentStartRegistrationProducesOnlyOneEmailSend()
    {
        var email = $"otp-concurrent-start-{Guid.NewGuid():N}@example.test";
        var sendCount = 0;
        var delayedSender = new BlockingTestSender(async () =>
        {
            Interlocked.Increment(ref sendCount);
            await Task.Delay(200);
        });

        using var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureServices(s => s.AddSingleton<ITransactionalEmailSender>(delayedSender));
        });
        using var client = app.CreateClient();

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => client.PostAsJsonAsync("/api/auth/register", new MemberRegistration("Concurrent User", email, "Password12345!")))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var successCount = results.Count(r => r.StatusCode == HttpStatusCode.OK);
        var cooldownCount = results.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);

        Assert.Equal(1, successCount);
        Assert.Equal(4, cooldownCount);
        Assert.Equal(1, sendCount);
    }

    [Fact]
    public async Task GoogleOnlyUserCanSetPasswordThroughEmailResetFlow()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var googleEmail = $"google-reset-{Guid.NewGuid():N}@example.test";
        var googleUserId = Guid.CreateVersion7();
        var sub = $"google-sub-{Guid.NewGuid():N}";

        // User registered with Google (password_hash is null)
        await using var cmd = source.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, role, status) " +
            "VALUES(@id, @email, NULL, 'Google Only User', 'CUSTOMER', 'ACTIVE');" +
            "INSERT INTO identity.external_identities(user_id, provider, subject, email) VALUES(@id, 'google', @sub, @email);");
        cmd.Parameters.AddWithValue("id", googleUserId);
        cmd.Parameters.AddWithValue("email", googleEmail);
        cmd.Parameters.AddWithValue("sub", sub);
        await cmd.ExecuteNonQueryAsync();

        using var client = factory.CreateClient();

        // 1. Calling /api/member/password with null/empty current password is not permitted
        var token = factory.Token("CUSTOMER", userId: googleUserId);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var directChangeResp = await client.PostAsJsonAsync("/api/member/password", new PasswordChange("", "NewPass123456!"));
        Assert.Equal(HttpStatusCode.BadRequest, directChangeResp.StatusCode);

        factory.GoogleValidator.Handler = (_, audience) => new GoogleJsonWebSignature.Payload
        {
            Audience = audience,
            Subject = sub,
            Email = googleEmail,
            EmailVerified = true,
            Name = "Google Only User"
        };
        using var googleClient = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var signedIn = await googleClient.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("validated-test-credential"));
        signedIn.EnsureSuccessStatusCode();
        var oldAccessCookie = signedIn.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("florabot_access=")).Split(';')[0];
        googleClient.DefaultRequestHeaders.Add("Cookie", oldAccessCookie);
        Assert.Equal(HttpStatusCode.OK, (await googleClient.GetAsync("/api/auth/me")).StatusCode);
        factory.GoogleValidator.Handler = null;

        // 2. Generate reset ticket via email proof: version for null hash is SHA256("")
        var version = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("")));
        var protector = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>().CreateProtector("member-password-reset-v1");
        var resetToken = protector.Protect(System.Text.Json.JsonSerializer.Serialize(new { UserId = googleUserId, Version = version, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20) }));

        // 3. Reset password sets first password
        client.DefaultRequestHeaders.Authorization = null;
        var resetResp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(resetToken, "NewPass123456!"));
        Assert.Equal(HttpStatusCode.NoContent, resetResp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await googleClient.GetAsync("/api/auth/me")).StatusCode);

        // 4. Token cannot be reused (replay fails)
        var replayResp = await client.PostAsJsonAsync("/api/auth/reset-password", new PasswordReset(resetToken, "NewPass123456!"));
        Assert.Equal(HttpStatusCode.BadRequest, replayResp.StatusCode);

        // 5. User can now log in using the newly set password
        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(googleEmail, "NewPass123456!"));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
    }

    [Fact]
    public async Task PrivilegedAccountEmailCollisionReturnsForbiddenWithoutLinking()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var adminEmail = $"admin-collision-{Guid.NewGuid():N}@example.test";
        var adminUserId = Guid.CreateVersion7();

        await using var cmd = source.CreateCommand(
            "INSERT INTO identity.users(id, email, password_hash, full_name, role, status) " +
            "VALUES(@id, @email, '$2b$12$N9qo8uLOickgx2ZMRZoMyeIjZAgcfl7p92ldGxad68LJZdL17lhWy', 'Admin Col', 'ADMIN', 'ACTIVE');");
        cmd.Parameters.AddWithValue("id", adminUserId);
        cmd.Parameters.AddWithValue("email", adminEmail);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            factory.GoogleValidator.Handler = (_, aud) => new GoogleJsonWebSignature.Payload
            {
                Audience = aud,
                EmailVerified = true,
                Subject = $"google-sub-{Guid.NewGuid():N}",
                Email = adminEmail
            };

            using var client = factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("admin-collision-token"));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            factory.GoogleValidator.Handler = null;
        }
    }

    [Fact]
    public async Task ActualRateLimitingIsPreservedWhenClientIpIsReused()
    {
        using var client = factory.CreateClient();
        var testIp = $"198.51.100.{Random.Shared.Next(1, 200)}";
        client.DefaultRequestHeaders.Add("X-Test-Client-IP", testIp);

        // The auth limiter allows 10 requests per minute from the same IP.
        for (var i = 0; i < 10; i++)
        {
            var okOrBad = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nonexistent@test.com", "pass"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, okOrBad.StatusCode);
        }

        // 11th request from the same IP is throttled
        var throttled = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nonexistent@test.com", "pass"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
    }

    [Fact]
    public async Task MissingGoogleClientIdReturns503()
    {
        using var unconfiguredApp = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("GOOGLE_CLIENT_ID", "");
            b.UseSetting("Authentication:Google:ClientId", "");
        });
        using var client = unconfiguredApp.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("any-valid-token-length-4096"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class BlockingTestSender(Func<Task> onSend) : ITransactionalEmailSender
    {
        public async Task SendAsync(TransactionalEmail email, CancellationToken ct)
        {
            await onSend();
        }
    }
}
