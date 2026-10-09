using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using FloraBot.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class CustomEndpointAccessTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Seller = "20000000-0000-0000-0000-000000000001";
    private const string ForeignSeller = "20000000-0000-0000-0000-000000000002";
    private const string Kiosk = "40000000-0000-0000-0000-000000000001";
    private const string ForeignKiosk = "40000000-0000-0000-0000-000000000002";
    private sealed record Access(string Method, string Template, string[] Roles)
    {
        public string Key => Method + " " + Template;
    }

    // Expected permissions are independent of endpoint metadata and the flow generator.
    private static readonly Access[] Matrix = BuildMatrix();

    [Fact]
    public async Task EveryCustomHttpEndpointHasExactlyTheExpectedPublicOrRoleAccess()
    {
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => IsCustomHttpEndpoint(endpoint))
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => (Key: method + " " + endpoint.RoutePattern.RawText, Endpoint: endpoint)))
            .ToDictionary(entry => entry.Key, entry => entry.Endpoint);
        Assert.Equal(Matrix.Select(entry => entry.Key).Order(), endpoints.Keys.Order());

        foreach (var expected in Matrix)
        {
            var endpoint = endpoints[expected.Key];
            var isPublic = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            Assert.True(isPublic == (expected.Roles.Length == 0), $"Public access changed: {expected.Key}");
            if (isPublic) continue;
            var policy = await AuthorizationPolicy.CombineAsync(provider, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.NotNull(policy);
            foreach (var role in new[] { "ADMIN", "STAFF", "SELLER", "CUSTOMER", "KIOSK", "SYSTEM", "ANONYMOUS" })
            {
                var http = new DefaultHttpContext();
                http.Request.RouteValues["sellerId"] = Seller;
                http.Request.RouteValues["kioskId"] = Kiosk;
                http.User = role == "ANONYMOUS" ? new(new ClaimsIdentity()) : new(new ClaimsIdentity(
                    [new("sub", Guid.NewGuid().ToString()), new("role", role), new("seller_id", Seller),
                        new("seller_status", "ACTIVE"), new(expected.Template.StartsWith("/api/kiosks/") ? "kiosk_id" : "test_kiosk", Kiosk)], "test", "sub", "role"));
                var result = await authorization.AuthorizeAsync(http.User, http, policy);
                Assert.True(result.Succeeded == expected.Roles.Contains(role), $"Unexpected {role} access: {expected.Key}");
            }
        }
    }

    [Fact]
    public async Task AnonymousRequestsCannotReachAnyPrivateCustomHttpEndpoint()
    {
        using var client = factory.CreateClient();
        foreach (var endpoint in Matrix.Where(entry => entry.Roles.Length > 0))
        {
            using var request = Request(endpoint);
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized,
                $"Expected anonymous 401: {endpoint.Key}; received {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task EveryCustomSellerRouteHidesForeignAndMissingTenants()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        var missing = Guid.NewGuid().ToString();
        foreach (var endpoint in Matrix.Where(entry => entry.Template.Contains("{sellerId:guid}")))
        {
            string? foreignBody = null;
            foreach (var seller in new[] { ForeignSeller, missing })
            {
                using var request = Request(endpoint, seller: seller);
                using var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound,
                    $"Expected cross-tenant 404: {endpoint.Key}; received {(int)response.StatusCode}");
                var body = await response.Content.ReadAsStringAsync();
                if (foreignBody is not null) Assert.Equal(foreignBody, body);
                foreignBody = body;
            }
        }
    }

    [Fact]
    public async Task EveryDeviceAccessibleCustomRouteRejectsAnotherKiosk()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        foreach (var endpoint in Matrix.Where(entry => entry.Template.Contains("{kioskId:guid}") && entry.Roles.Contains("KIOSK")))
        {
            using var request = Request(endpoint, kiosk: ForeignKiosk);
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                $"Expected foreign-device 403: {endpoint.Key}; received {(int)response.StatusCode}");
        }
    }

    private static bool IsCustomHttpEndpoint(RouteEndpoint endpoint)
    {
        var path = endpoint.RoutePattern.RawText ?? "";
        var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        return (path.StartsWith("/api/", StringComparison.Ordinal) || path.StartsWith("/openapi/", StringComparison.Ordinal) || path == "/health")
            && (name is null || !FlowExecutor.Definitions.ContainsKey(name));
    }

    private static HttpRequestMessage Request(Access endpoint, string seller = Seller, string kiosk = Kiosk)
    {
        var path = Regex.Replace(endpoint.Template, @"\{([^}:]+)(?::[^}]+)?\}", match => match.Groups[1].Value switch
        {
            "sellerId" => seller,
            "kioskId" => kiosk,
            "documentName" => "v1",
            _ => "99999999-9999-4999-8999-999999999999"
        });
        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), path);
        if (endpoint.Method is "POST" or "PUT")
        {
            if (endpoint.Template.EndsWith("/photos", StringComparison.Ordinal) || endpoint.Template.Contains("/evidence", StringComparison.Ordinal))
            {
                request.Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jN1sAAAAASUVORK5CYII="));
                request.Content.Headers.ContentType = new("image/png");
                return request;
            }
            var json = endpoint.Template.EndsWith("/otp/request", StringComparison.Ordinal) ? "{\"phone\":\"0901234567\"}" :
                endpoint.Template.EndsWith("/otp/verify", StringComparison.Ordinal) ? "{\"phone\":\"0901234567\",\"code\":\"123456\"}" : "{}";
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static Access[] BuildMatrix()
    {
        var entries = new List<Access>();
        Add([], "GET", "/api/shop/catalog");
        Add(["STAFF"], "POST", "/api/staff/tasks/{taskId:guid}/resolve-incident");
        Add(["STAFF"], "POST", "/api/staff/tasks/{taskId:guid}/evidence/upload", "/api/staff/tasks/{taskId:guid}/evidence");
        Add(["STAFF"], "GET", "/api/staff/tasks/{taskId:guid}/evidence", "/api/staff/tasks/{taskId:guid}/evidence/{attachmentId:guid}");
        Add(["ADMIN"], "GET", "/api/admin/staff-tasks/{taskId:guid}/evidence", "/api/admin/staff-tasks/{taskId:guid}/evidence/{attachmentId:guid}");
        Add(["ADMIN"], "GET", "/api/admin/staff", "/api/admin/staff-tasks", "/api/admin/staff-delivery-scopes");
        Add(["ADMIN"], "POST", "/api/admin/staff-tasks", "/api/admin/staff-tasks/{taskId:guid}/transition", "/api/admin/staff-tasks/{taskId:guid}/reassign");
        Add(["STAFF"], "GET", "/api/staff/tasks", "/api/staff/tasks/{taskId:guid}");
        Add(["ADMIN"], "GET", "/api/admin/staff-tasks/{taskId:guid}");
        Add(["STAFF"], "POST", "/api/staff/tasks/{taskId:guid}/transition");
        Add(["STAFF"], "GET", "/api/staff/tasks/{taskId:guid}/stock");
        Add(["STAFF"], "POST", "/api/staff/tasks/{taskId:guid}/stock");
        Add([], "POST", "/api/auth/register", "/api/auth/forgot-password", "/api/auth/reset-password");
        Add(["ADMIN", "STAFF", "SELLER", "CUSTOMER"], "GET", "/api/auth/me");
        Add(["CUSTOMER", "SELLER"], "GET", "/api/member/profile");
        Add(["CUSTOMER", "SELLER"], "POST", "/api/member/profile", "/api/member/password", "/api/member/shop");
        Add(["CUSTOMER", "SELLER"], "GET", "/api/member/history", "/api/member/preorders");
        Add(["CUSTOMER", "SELLER"], "POST", "/api/member/preorders", "/api/member/preorders/{requestId:guid}/accept", "/api/member/preorders/{requestId:guid}/cancel", "/api/member/preorders/{requestId:guid}/payment-link");
        Add(["ADMIN", "SELLER"], "GET", "/api/sellers/{sellerId:guid}/preorders");
        Add(["ADMIN", "SELLER"], "POST", "/api/sellers/{sellerId:guid}/preorders/{requestId:guid}/quote", "/api/sellers/{sellerId:guid}/preorders/{requestId:guid}/fulfill", "/api/sellers/{sellerId:guid}/preorders/{requestId:guid}/cancel");
        Add(["ADMIN"], "GET", "/api/admin/preorders");
        Add(["ADMIN"], "POST", "/api/admin/preorders/{requestId:guid}/cancel");
        Add([], "POST", "/api/receipts/{orderId:guid}/evidence");
        Add(["ADMIN"], "GET", "/api/admin/evidence/{attachmentId:guid}");
        Add(["ADMIN"], "POST", "/api/admin/evidence/{purpose}/{resourceId:guid}");
        Add(["ADMIN"], "GET", "/api/admin/evidence/{purpose}/{resourceId:guid}");
        Add(["ADMIN", "SELLER"], "POST", "/api/sellers/{sellerId:guid}/slots/{slotId:guid}/evidence/{purpose}");
        Add([], "GET", "/health", "/api/packages", "/api/kiosks");
        Add([], "POST", "/api/auth/login", "/api/auth/admin/login", "/api/auth/refresh", "/api/auth/logout", "/api/sellers", "/api/receipts/lookup", "/api/payments/webhook");
        Add(["ADMIN"], "GET", "/openapi/{documentName}.json",
            "/api/admin/sellers", "/api/admin/slots",
            "/api/admin/reports/v_admin_queue", "/api/admin/reports/v_refund_queue", "/api/admin/reports/v_revenue_daily",
            "/api/admin/reports/v_platform_revenue", "/api/admin/reports/v_ai_effectiveness",
            "/api/admin/incidents", "/api/admin/incidents/{incidentId:guid}",
            "/api/admin/complaints", "/api/admin/complaints/{complaintId:guid}",
            "/api/admin/refunds", "/api/admin/refunds/{refundId:guid}",
            "/api/admin/withdrawals", "/api/admin/withdrawals/{withdrawalId:guid}");
        Add(["ADMIN"], "POST", "/api/admin/refunds/{refundId:guid}/bank-details", "/api/admin/withdrawals/{withdrawalId:guid}/bank-details");
        Add(["ADMIN", "SELLER"], "GET", "/api/sellers/{sellerId:guid}",
            "/api/sellers/{sellerId:guid}/products", "/api/sellers/{sellerId:guid}/subscriptions",
            "/api/sellers/{sellerId:guid}/slots", "/api/sellers/{sellerId:guid}/wallet",
            "/api/sellers/{sellerId:guid}/bank", "/api/sellers/{sellerId:guid}/accessories",
            "/api/sellers/{sellerId:guid}/products/{productId:guid}/photos");
        Add(["ADMIN", "SELLER"], "POST", "/api/sellers/{sellerId:guid}/products/{productId:guid}/photos",
            "/api/sellers/{sellerId:guid}/subscriptions/{subscriptionId:guid}/payment-link");
        Add(["SELLER"], "POST", "/api/sellers/{sellerId:guid}/products");
        Add(["SELLER"], "PUT", "/api/sellers/{sellerId:guid}/products/{productId:guid}");
        Add(["KIOSK"], "POST", "/api/kiosks/{kioskId:guid}/otp/request", "/api/kiosks/{kioskId:guid}/otp/verify");
        Add(["KIOSK", "CUSTOMER", "SELLER"], "GET", "/api/kiosks/{kioskId:guid}/catalog", "/api/kiosks/{kioskId:guid}/catalog/items",
            "/api/kiosks/{kioskId:guid}/catalog/accessories", "/api/kiosks/{kioskId:guid}/checkouts/{checkoutId:guid}",
            "/api/kiosks/{kioskId:guid}/surveys/{surveyId:guid}");
        Add(["KIOSK", "CUSTOMER", "SELLER"], "POST", "/api/kiosks/{kioskId:guid}/checkouts/{checkoutId:guid}/payment-link");
        Add(["CUSTOMER", "SELLER"], "GET", "/api/kiosks/{kioskId:guid}/customer/history", "/api/kiosks/{kioskId:guid}/customer/points");
        return entries.ToArray();

        void Add(string[] roles, string method, params string[] paths)
        {
            entries.AddRange(paths.Select(path => new Access(method, path, roles)));
        }
    }
}
