using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FloraBot.Api.Tests;

public sealed class GoogleAuthAndHierarchyTests
{
    [Fact]
    public async Task GoogleAuthCreatesCustomerAccountAndIssuesCookies()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var email = $"google-{Guid.NewGuid():N}@gmail.com";
        var mockToken = $"mock-google-token:{email}:Nguyen Van Customer";

        var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest(mockToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.NotNull(session);
        Assert.Equal("CUSTOMER", session.Role);
        Assert.Equal("Nguyen Van Customer", session.Name);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Contains(cookies, c => c.StartsWith("florabot_access=", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("florabot_refresh=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GoogleAuthRejectsInvalidToken()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleAuthRequest("invalid-untrusted-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
        var createResp = await client.PostAsJsonAsync("/api/admin/managers", new CreateAccountRequest(opsEmail, "Operations Lead", "StrongPass123!", "0901234567"));
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
    }

    [Fact]
    public async Task OperationsManagerCanCreateTechnician()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        var opsToken = factory.Token("OPERATIONS_MANAGER");
        client.DefaultRequestHeaders.Authorization = new("Bearer", opsToken);

        var techEmail = $"tech-{Guid.NewGuid():N}@invalid.test";
        var createResp = await client.PostAsJsonAsync("/api/operations/technicians", new CreateAccountRequest(techEmail, "Senior Technician", "StrongPass123!", "0987654321"));
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);

        var createdTech = await createResp.Content.ReadFromJsonAsync<AccountDirectoryItem>();
        Assert.NotNull(createdTech);
        Assert.Equal("TECHNICIAN", createdTech.Role);
        Assert.Equal("ACTIVE", createdTech.Status);
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

        // 2. Cannot create staff for other shop
        var deniedResp = await client.PostAsJsonAsync($"/api/sellers/{otherShopId}/staff", new CreateAccountRequest($"staff-other-{Guid.NewGuid():N}@invalid.test", "Other Staff", "StrongPass123!"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedResp.StatusCode);
    }
}
