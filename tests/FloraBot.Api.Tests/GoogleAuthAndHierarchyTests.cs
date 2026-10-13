using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FloraBot.Api.Tests;

public sealed class GoogleAuthAndHierarchyTests
{
    private static string UniquePhone() => $"09{Random.Shared.Next(10000000, 99999999)}";

    [Fact]
    public async Task GoogleAuthCreatesCustomerAccountAndIssuesCookies()
    {
        await using var factory = new ApiFactory();
        var email = $"google-{Guid.NewGuid():N}@gmail.com";
        var name = "Nguyen Van Customer";
        var token = "simulated-valid-google-id-token";

        factory.GoogleValidator.Handler = (tok, aud) =>
        {
            if (tok == token)
            {
                return new GoogleJsonWebSignature.Payload
                {
                    Subject = $"google-sub-{Guid.NewGuid():N}",
                    Email = email,
                    EmailVerified = true,
                    Name = name
                };
            }
            throw new InvalidJwtException("Invalid token");
        };

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(session);
        Assert.Equal("CUSTOMER", session.Role);
        Assert.Equal(name, session.Name);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.StartsWith("florabot_access=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GoogleAuthRejectsInvalidToken()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        // 1. Random untrusted token is rejected
        var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("invalid-untrusted-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // 2. Legacy magic mock token is strictly rejected (no ALLOW_TEST_AUTH backdoor in production/testing)
        var mockResponse = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("mock-google-token:attacker@evil.test:Hacker"));
        Assert.Equal(HttpStatusCode.Unauthorized, mockResponse.StatusCode);
    }

    [Fact]
    public async Task AdminCanCreateOperationsManagerAndNonAdminIsForbidden()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        // 1. Non-admin is forbidden
        var sellerToken = factory.Token("SELLER");
        client.DefaultRequestHeaders.Authorization = new("Bearer", sellerToken);
        var forbiddenResp = await client.PostAsJsonAsync("/api/admin/managers", new CreateAccountRequest("ops@invalid.test", "Manager A", "StrongPass123!"));
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);

        // 2. Admin can create Operations Manager
        var adminToken = factory.Token("ADMIN");
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);

        var opsEmail = $"ops-{Guid.NewGuid():N}@invalid.test";
        var createResp = await client.PostAsJsonAsync("/api/admin/managers", new CreateAccountRequest(opsEmail, "Operations Lead", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var createdManager = await createResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(createdManager);
        Assert.Equal("OPERATIONS_MANAGER", createdManager.Role);
        Assert.Equal("ACTIVE", createdManager.Status);
        Assert.Equal(opsEmail, createdManager.Email);

        // 3. Manager appears in manager list
        var listResp = await client.GetAsync("/api/admin/managers");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<List<AccountDirectoryItem>>();
        Assert.NotNull(list);
        Assert.Contains(list, m => m.Id == createdManager.Id);

        // 4. Admin can update status
        var statusResp = await client.PatchAsJsonAsync($"/api/admin/managers/{createdManager.Id}/status", new UpdateAccountStatusRequest("LOCKED"));
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var updated = await statusResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(updated);
        Assert.Equal("LOCKED", updated.Status);
    }

    [Fact]
    public async Task OperationsManagerCanCreateTechnician()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        // 1. Admin creates an Operations Manager first
        var adminToken = factory.Token("ADMIN");
        client.DefaultRequestHeaders.Authorization = new("Bearer", adminToken);

        var opsEmail = $"ops-{Guid.NewGuid():N}@invalid.test";
        var opsCreateResp = await client.PostAsJsonAsync("/api/admin/managers", new CreateAccountRequest(opsEmail, "Active Ops Lead", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, opsCreateResp.StatusCode);
        var opsManager = await opsCreateResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(opsManager);

        // 2. Operations Manager creates a Technician
        var opsToken = factory.Token("OPERATIONS_MANAGER", userId: opsManager.Id);
        client.DefaultRequestHeaders.Authorization = new("Bearer", opsToken);

        var techEmail = $"tech-{Guid.NewGuid():N}@invalid.test";
        var createResp = await client.PostAsJsonAsync("/api/operations/technicians", new CreateAccountRequest(techEmail, "Senior Technician", "StrongPass123!", UniquePhone()));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var createdTech = await createResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(createdTech);
        Assert.Equal("TECHNICIAN", createdTech.Role);
        Assert.Equal("ACTIVE", createdTech.Status);

        // 3. Technician appears in technician list
        var listResp = await client.GetAsync("/api/operations/technicians");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<List<AccountDirectoryItem>>();
        Assert.NotNull(list);
        Assert.Contains(list, t => t.Id == createdTech.Id);

        // 4. Operations Manager can update technician status
        var statusResp = await client.PatchAsJsonAsync($"/api/operations/technicians/{createdTech.Id}/status", new UpdateAccountStatusRequest("DISABLED"));
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var updatedTech = await statusResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(updatedTech);
        Assert.Equal("DISABLED", updatedTech.Status);
    }

    [Fact]
    public async Task SellerCanCreateStaffOnlyForOwnShop()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var myShopId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var otherShopId = Guid.Parse("20000000-0000-0000-0000-000000000002");

        var sellerToken = factory.Token("SELLER", sellerId: myShopId);
        client.DefaultRequestHeaders.Authorization = new("Bearer", sellerToken);

        // 1. Can create staff for own shop
        var staffEmail = $"staff-{Guid.NewGuid():N}@invalid.test";
        var createResp = await client.PostAsJsonAsync($"/api/sellers/{myShopId}/staff", new CreateAccountRequest(staffEmail, "Florist Staff", "StrongPass123!"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var createdStaff = await createResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(createdStaff);
        Assert.Equal("SELLER_STAFF", createdStaff.Role);

        // 2. Cannot create staff for other shop (SameSeller masks foreign tenant as 404 NotFound)
        var deniedResp = await client.PostAsJsonAsync($"/api/sellers/{otherShopId}/staff", new CreateAccountRequest($"staff-other-{Guid.NewGuid():N}@invalid.test", "Other Staff", "StrongPass123!"));
        Assert.Equal(HttpStatusCode.NotFound, deniedResp.StatusCode);

        // 3. Can update own staff status
        var updateResp = await client.PatchAsJsonAsync($"/api/sellers/{myShopId}/staff/{createdStaff.Id}/status", new UpdateAccountStatusRequest("LOCKED"));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updatedStaff = await updateResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(updatedStaff);
        Assert.Equal("LOCKED", updatedStaff.Status);

        // 4. Cannot update staff for other shop (SameSeller masks as 404 NotFound)
        var crossUpdateResp = await client.PatchAsJsonAsync($"/api/sellers/{otherShopId}/staff/{createdStaff.Id}/status", new UpdateAccountStatusRequest("ACTIVE"));
        Assert.Equal(HttpStatusCode.NotFound, crossUpdateResp.StatusCode);
    }
}
