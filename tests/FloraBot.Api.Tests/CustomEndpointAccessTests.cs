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
            foreach (var role in new[] { "ADMIN", "STAFF", "SELLER", "CUSTOMER", "OPERATIONS_MANAGER", "TECHNICIAN", "SELLER_STAFF", "KIOSK", "SYSTEM", "ANONYMOUS" })
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
        if (endpoint.Method is "POST" or "PUT" or "PATCH")
        {
            if (endpoint.Template.EndsWith("/photos", StringComparison.Ordinal) || endpoint.Template.Contains("/evidence", StringComparison.Ordinal))
            {
                request.Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jN1sAAAAASUVORK5CYII="));
                request.Content.Headers.ContentType = new("image/png");
                return request;
            }
            var json = endpoint.Template.EndsWith("/otp/request", StringComparison.Ordinal) ? "{\"phone\":\"0901234567\"}" :
                endpoint.Template.EndsWith("/otp/verify", StringComparison.Ordinal) ? "{\"phone\":\"0901234567\",\"code\":\"123456\"}" :
                endpoint.Template.EndsWith("/status", StringComparison.Ordinal) ? "{\"status\":\"ACTIVE\"}" : "{}";
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return request;
    }

    private static Access[] BuildMatrix()
    {
        var entries = new List<Access>();
        Add([], "GET", "/api/shop/catalog");
        Add([], "GET", "/api/kiosk-locations");
        Add(["SELLER"], "GET", "/api/sellers/{sellerId:guid}/kiosks");
        Add(["SELLER"], "PUT", "/api/sellers/{sellerId:guid}/kiosks/{kioskId:guid}/location");
        Add(["TECHNICIAN"], "POST", "/api/staff/tasks/{taskId:guid}/resolve-incident");
        Add(["TECHNICIAN", "SELLER_STAFF"], "POST", "/api/staff/tasks/{taskId:guid}/evidence/upload", "/api/staff/tasks/{taskId:guid}/evidence");
        Add(["TECHNICIAN", "SELLER_STAFF"], "GET", "/api/staff/tasks/{taskId:guid}/evidence", "/api/staff/tasks/{taskId:guid}/evidence/{attachmentId:guid}");
        Add(["ADMIN"], "GET", "/api/admin/staff-tasks/{taskId:guid}/evidence", "/api/admin/staff-tasks/{taskId:guid}/evidence/{attachmentId:guid}");
        Add(["OPERATIONS_MANAGER"], "GET", "/api/operations/staff-tasks/{taskId:guid}/evidence", "/api/operations/staff-tasks/{taskId:guid}/evidence/{attachmentId:guid}");
        Add(["ADMIN", "SELLER"], "GET", "/api/sellers/{sellerId:guid}/staff-tasks/{taskId:guid}/evidence", "/api/sellers/{sellerId:guid}/staff-tasks/{taskId:guid}/evidence/{attachmentId:guid}");
        Add(["ADMIN"], "GET", "/api/admin/staff", "/api/admin/staff-tasks", "/api/admin/staff-delivery-scopes", "/api/admin/managers");
        Add(["ADMIN"], "POST", "/api/admin/managers");
        Add(["ADMIN"], "PATCH", "/api/admin/managers/{id:guid}/status");
        Add(["OPERATIONS_MANAGER"], "GET", "/api/operations/staff", "/api/operations/incidents", "/api/operations/staff-tasks", "/api/operations/technicians");
        Add(["OPERATIONS_MANAGER"], "POST", "/api/operations/technicians");
        Add(["OPERATIONS_MANAGER"], "PATCH", "/api/operations/technicians/{id:guid}/status");
        Add(["ADMIN", "SELLER"], "GET", "/api/sellers/{sellerId:guid}/staff", "/api/sellers/{sellerId:guid}/staff-delivery-scopes", "/api/sellers/{sellerId:guid}/staff-tasks");
        Add(["SELLER"], "GET", "/api/sellers/{sellerId:guid}/staff/accounts");
        Add(["SELLER"], "POST", "/api/sellers/{sellerId:guid}/staff");
        Add(["SELLER"], "PATCH", "/api/sellers/{sellerId:guid}/staff/{staffId:guid}/status");
        Add(["ADMIN"], "POST", "/api/admin/staff-tasks", "/api/admin/staff-tasks/{taskId:guid}/transition", "/api/admin/staff-tasks/{taskId:guid}/reassign");
        Add(["OPERATIONS_MANAGER"], "POST", "/api/operations/staff-tasks", "/api/operations/staff-tasks/{taskId:guid}/transition", "/api/operations/staff-tasks/{taskId:guid}/reassign");
        Add(["ADMIN", "SELLER"], "POST", "/api/sellers/{sellerId:guid}/staff-tasks", "/api/sellers/{sellerId:guid}/staff-tasks/{taskId:guid}/transition", "/api/sellers/{sellerId:guid}/staff-tasks/{taskId:guid}/reassign");
        Add(["TECHNICIAN", "SELLER_STAFF"], "GET", "/api/staff/tasks", "/api/staff/tasks/{taskId:guid}");
        Add(["ADMIN"], "GET", "/api/admin/staff-tasks/{taskId:guid}");
        Add(["OPERATIONS_MANAGER"], "GET", "/api/operations/staff-tasks/{taskId:guid}");
        Add(["ADMIN", "SELLER"], "GET", "/api/sellers/{sellerId:guid}/staff-tasks/{taskId:guid}");
        Add(["TECHNICIAN", "SELLER_STAFF"], "POST", "/api/staff/tasks/{taskId:guid}/transition");
        Add(["SELLER_STAFF"], "GET", "/api/staff/tasks/{taskId:guid}/stock");
        Add(["SELLER_STAFF"], "POST", "/api/staff/tasks/{taskId:guid}/stock");
        Add([], "POST", "/api/auth/register", "/api/auth/register/verify", "/api/auth/google", "/api/auth/forgot-password", "/api/auth/reset-password");
        Add(["ADMIN", "SELLER", "CUSTOMER", "OPERATIONS_MANAGER", "TECHNICIAN", "SELLER_STAFF"], "GET", "/api/auth/me", "/api/account/profile");
        Add(["ADMIN", "SELLER", "CUSTOMER", "OPERATIONS_MANAGER", "TECHNICIAN", "SELLER_STAFF"], "PUT", "/api/account/profile");
        Add(["ADMIN", "SELLER", "CUSTOMER", "OPERATIONS_MANAGER", "TECHNICIAN", "SELLER_STAFF"], "POST",
            "/api/account/profile/bank/reveal",
            "/api/account/contact-email",
            "/api/account/contact-email/verify",
            "/api/account/contact-email/reset");
        Add(["CUSTOMER"], "POST", "/api/member/chats/open");
        Add(["CUSTOMER"], "GET", "/api/member/chats");
        Add(["SELLER"], "GET", "/api/sellers/{sellerId:guid}/chats");
        Add(["ADMIN", "STAFF", "SELLER", "CUSTOMER", "OPERATIONS_MANAGER", "TECHNICIAN", "SELLER_STAFF", "KIOSK", "SYSTEM"], "GET",
            "/api/chats/{chatId:guid}/messages",
            "/api/chats/{chatId:guid}/messages/{messageId:guid}/image");
        Add(["ADMIN", "STAFF", "SELLER", "CUSTOMER", "OPERATIONS_MANAGER", "TECHNICIAN", "SELLER_STAFF", "KIOSK", "SYSTEM"], "POST",
            "/api/chats/{chatId:guid}/messages",
            "/api/chats/{chatId:guid}/images",
            "/api/chats/{chatId:guid}/read");
        Add(["CUSTOMER", "SELLER"], "GET", "/api/member/profile");
        Add(["CUSTOMER", "SELLER"], "POST", "/api/member/profile", "/api/member/password", "/api/member/shop", "/api/member/google/link");
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
        Add([], "GET", "/health", "/api/packages", "/api/kiosks", "/api/auth/config", "/api/kiosk-packages");
        Add(["CUSTOMER"], "POST", "/api/member/kiosk-applications", "/api/member/kiosk-applications/{id:guid}/payment-link", "/api/member/kiosk-applications/{id:guid}/cancel");
        Add(["CUSTOMER", "SELLER"], "GET", "/api/member/kiosk-applications/current");
        Add(["CUSTOMER", "SELLER", "ADMIN"], "POST", "/api/member/kiosk-applications/{id:guid}/reconcile");
        Add(["ADMIN", "OPERATIONS_MANAGER"], "GET", "/api/operations/kiosk-applications", "/api/operations/kiosk-applications/{id:guid}", "/api/operations/kiosk-applications/{id:guid}/evidence/{attachmentId:guid}");
        Add(["OPERATIONS_MANAGER"], "POST", "/api/operations/kiosk-applications/{id:guid}/confirm", "/api/operations/kiosk-applications/{id:guid}/schedule", "/api/operations/kiosk-applications/{id:guid}/rework", "/api/operations/kiosk-applications/{id:guid}/handover", "/api/operations/kiosk-applications/{id:guid}/reject", "/api/operations/kiosk-applications/{id:guid}/platform-failure", "/api/operations/kiosks/enroll", "/api/operations/kiosks/{id:guid}/provision-key");
        Add(["OPERATIONS_MANAGER"], "GET", "/api/operations/eligible-kiosks");
        Add(["TECHNICIAN"], "GET", "/api/staff/kiosk-installations", "/api/staff/kiosk-installations/{id:guid}", "/api/staff/kiosk-installations/{id:guid}/evidence", "/api/staff/kiosk-installations/{id:guid}/evidence/{attachmentId:guid}");
        Add(["TECHNICIAN"], "POST", "/api/staff/kiosk-installations/{id:guid}/start", "/api/staff/kiosk-installations/{id:guid}/evidence/upload", "/api/staff/kiosk-installations/{id:guid}/evidence", "/api/staff/kiosk-installations/{id:guid}/submit");
        Add(["ADMIN"], "GET", "/api/admin/kiosk-applications/refunds", "/api/admin/kiosk-applications/{id:guid}/refund/proof/{attachmentId:guid}");
        Add(["ADMIN"], "POST", "/api/admin/kiosk-applications/{id:guid}/refund/approve", "/api/admin/kiosk-applications/{id:guid}/refund/confirm", "/api/admin/kiosk-applications/{id:guid}/refund/bank-details", "/api/admin/kiosk-applications/{id:guid}/refund/evidence/upload");
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
            "/api/sellers/{sellerId:guid}/subscriptions",
            "/api/sellers/{sellerId:guid}/wallet",
            "/api/sellers/{sellerId:guid}/bank", "/api/sellers/{sellerId:guid}/accessories",
            "/api/me/products/{productId:guid}/photos");
        Add(["ADMIN", "SELLER"], "POST", "/api/me/products/{productId:guid}/photos",
            "/api/sellers/{sellerId:guid}/subscriptions/{subscriptionId:guid}/payment-link");
        Add(["SELLER"], "GET", "/api/me/products");
        Add(["SELLER"], "GET", "/api/me/kiosks/{kioskId:guid}/slots");
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
