using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class AuthorizationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private HttpClient Seller()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token());
        return client;
    }
    [Fact]
    public async Task SellerCannotReadAnotherSeller()
    {
        using var client = Seller();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sellers/20000000-0000-0000-0000-000000000002/products")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sellers/20000000-0000-0000-0000-000000000002/subscriptions")).StatusCode);
    }
    [Fact]
    public async Task SellerCannotExecuteAdminCommand()
    {
        using var client = Seller();
        var response = await client.PostAsJsonAsync("/api/admin/flows/set_cfg", new { p_key = "hold_minutes", p_value = 9 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Fact]
    public async Task DeviceCannotImpersonateAnotherKiosk()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/kiosks/40000000-0000-0000-0000-000000000002/flows/kiosk_heartbeat", new { })).StatusCode);
    }
    [Fact]
    public async Task ExpiredJwtIsRejected()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(expires: DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
    [Fact]
    public async Task SellerCannotMutateForeignResourceViaOwnRoute()
    {
        using var client = Seller();
        var response = await client.PostAsJsonAsync("/api/sellers/20000000-0000-0000-0000-000000000001/flows/update_product_price",
            new { p_product = "30000000-0000-0000-0000-000000000003", p_price = 1 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    [Fact]
    public async Task GlobalFilterProtectsUnscopedQueries()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        db.HttpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("role", "SELLER"), new Claim("seller_id", "20000000-0000-0000-0000-000000000001")], "test", "sub", "role"))
            }
        };
        var products = await db.FlowerProducts.AsNoTracking().ToListAsync();
        Assert.NotEmpty(products);
        Assert.All(products, p => Assert.Equal(Guid.Parse("20000000-0000-0000-0000-000000000001"), p.SellerId));
    }
    [Fact]
    public void AllTenantEntitiesHaveGlobalFilters()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        foreach (var entity in db.Model.GetEntityTypes().Where(e => e.FindProperty("SellerId") is not null || e.FindProperty("CurrentSellerId") is not null || e.ClrType == typeof(Seller)))
            Assert.NotEmpty(entity.GetDeclaredQueryFilters());
    }
    [Fact]
    public void AnonymousEndpointsMatchExplicitAllowlist()
    {
        _ = factory.CreateClient();
        var allowed = new HashSet<string> { "/api/shop/catalog", "/api/auth/register", "/api/auth/forgot-password", "/api/auth/reset-password", "/health", "/api/auth/login", "/api/auth/refresh", "/api/auth/logout", "/api/packages", "/api/kiosks",
            "/api/receipts/lookup", "/api/receipts/{orderId:guid}/evidence", "/api/receipts/flows/open_dispute", "/api/receipts/flows/submit_refund_info", "/api/sellers", "/api/payments/webhook" };
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var route in routes.Where(r => r.Metadata.GetMetadata<IAllowAnonymous>() is not null)) Assert.Contains(route.RoutePattern.RawText!, allowed);
        foreach (var route in routes.Where(r => r.Metadata.GetMetadata<IAllowAnonymous>() is null)) Assert.NotEmpty(route.Metadata.GetOrderedMetadata<IAuthorizeData>());
    }
}
