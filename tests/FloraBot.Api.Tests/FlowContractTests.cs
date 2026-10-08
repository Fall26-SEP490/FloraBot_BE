using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class FlowContractTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task EveryCommandHidesServerClockAndActorFieldsFromOpenApi()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var seen = new HashSet<string>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            if (!path.Value.TryGetProperty("post", out var operation) || !operation.TryGetProperty("operationId", out var id)) continue;
            var name = id.GetString()!;
            if (!FlowExecutor.Definitions.ContainsKey(name)) continue;
            Assert.True(seen.Add(name), $"Duplicate operation for {name}");
            var schema = operation.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            if (schema.TryGetProperty("$ref", out var reference)) schema = schemas.GetProperty(reference.GetString()!.Split('/')[^1]);
            if (!schema.TryGetProperty("properties", out var properties)) continue;
            foreach (var field in new[] { "p_now", "p_user", "p_admin", "p_actor", "p_staff", "p_reporter", "p_approver" })
                Assert.False(properties.TryGetProperty(field, out _), $"Server-owned field {field} exposed by {name}");
        }
        Assert.Equal(FlowExecutor.Definitions.Keys.Order(), seen.Order());
    }

    [Fact]
    public async Task EveryGeneratedCommandMatchesTheInstalledSqlSignature()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var query = source.CreateCommand("""
            SELECT p.proname, p.proretset,
              coalesce(jsonb_agg(jsonb_build_object(
                'name', p.proargnames[a.ordinality::int],
                'type', format_type(a.type_oid, NULL),
                'optional', a.ordinality > p.pronargs - p.pronargdefaults
              ) ORDER BY a.ordinality) FILTER (WHERE a.type_oid IS NOT NULL), '[]'::jsonb)
            FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            LEFT JOIN LATERAL unnest(p.proargtypes::oid[]) WITH ORDINALITY a(type_oid, ordinality) ON true
            WHERE n.nspname='flow' AND p.prokind='f' AND p.proname=ANY(@names)
            GROUP BY p.oid ORDER BY p.proname
            """);
        query.Parameters.AddWithValue("names", FlowExecutor.Definitions.Keys.ToArray());
        await using var reader = await query.ExecuteReaderAsync();
        var seen = new HashSet<string>();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            Assert.True(seen.Add(name), $"Ambiguous SQL overload for {name}");
            var definition = FlowExecutor.Definitions[name];
            Assert.True(definition.SetReturning == reader.GetBoolean(1), $"Return cardinality differs for {name}");
            using var json = JsonDocument.Parse(reader.GetString(2));
            var installed = json.RootElement.EnumerateArray().Select(parameter => new FlowParameter(
                parameter.GetProperty("name").GetString()!,
                parameter.GetProperty("type").GetString()! switch
                {
                    "integer" => "int",
                    "timestamp with time zone" => "timestamptz",
                    var type => type
                }, parameter.GetProperty("optional").GetBoolean())).ToArray();
            Assert.True(definition.Params.SequenceEqual(installed), $"SQL parameter contract differs for {name}: {JsonSerializer.Serialize(installed)}");
        }
        Assert.Equal(FlowExecutor.Definitions.Keys.Order(), seen.Order());
    }

    [Fact]
    public async Task CommandRoleMatrixMatchesTheBriefAndApprovedSupplementalFlows()
    {
        // Explicit expected permissions: do not derive these from the generator's scope field.
        var expected = new Dictionary<string, string[]>();
        Add(["ADMIN"], "approve_seller set_seller_status assign_slot admin_dispose_slot resolve_device_fault set_kiosk_status admin_close_door set_cfg resolve_dispute approve_withdrawal pay_withdrawal reject_withdrawal admin_refund create_refund approve_refund confirm_refund reconcile_gateway reconcile_daily attach");
        Add(["ADMIN", "SELLER"], "set_seller_bank set_seller_tone subscribe set_product_status update_product_price add_product_photo restock_accessory release_slot stock_bouquet return_to_seller report_device_fault");
        Add(["SELLER"], "request_withdrawal");
        Add(["KIOSK"], "kiosk_heartbeat device_event");
        Add(["CUSTOMER"], "forget_customer");
        Add(["KIOSK", "CUSTOMER", "SELLER"], "kiosk_checkout request_pickup ai_suggest");
        Add([], "open_dispute submit_refund_info");
        Assert.Equal(FlowExecutor.Definitions.Keys.Order(), expected.Keys.Order());

        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        const string sellerId = "20000000-0000-0000-0000-000000000001";
        const string kioskId = "40000000-0000-0000-0000-000000000001";
        foreach (var (name, roles) in expected)
        {
            var endpoint = Assert.Single(routes, route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!;
            Assert.Equal(["POST"], methods.HttpMethods);
            if (roles.Length == 0)
            {
                Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
                continue; // Receipt capabilities have separate HTTP/security tests.
            }
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            var policy = await AuthorizationPolicy.CombineAsync(provider, endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.NotNull(policy);
            foreach (var role in new[] { "ADMIN", "SELLER", "CUSTOMER", "KIOSK", "UNAUTHENTICATED" })
            {
                var http = new DefaultHttpContext();
                http.Request.RouteValues["sellerId"] = sellerId;
                http.Request.RouteValues["kioskId"] = kioskId;
                http.User = role == "UNAUTHENTICATED" ? new ClaimsPrincipal(new ClaimsIdentity()) : new ClaimsPrincipal(new ClaimsIdentity(
                    [new("sub", Guid.NewGuid().ToString()), new("role", role), new("seller_id", sellerId), new("seller_status", "ACTIVE"), new(endpoint.RoutePattern.RawText!.StartsWith("/api/kiosks/") ? "kiosk_id" : "test_kiosk", kioskId)], "test", "sub", "role"));
                var result = await authorization.AuthorizeAsync(http.User, http, policy);
                Assert.True(result.Succeeded == roles.Contains(role), $"Unexpected {role} permission for {name}");
                if (role == "SELLER" && roles.Contains(role) && endpoint.RoutePattern.RawText!.StartsWith("/api/sellers/"))
                {
                    http.Request.RouteValues["sellerId"] = Guid.NewGuid().ToString();
                    Assert.False((await authorization.AuthorizeAsync(http.User, http, policy)).Succeeded, $"Missing tenant boundary for {name}");
                }
            }
        }

        void Add(string[] roles, string names)
        {
            foreach (var name in names.Split(' ')) expected.Add(name, roles);
        }
    }
}
