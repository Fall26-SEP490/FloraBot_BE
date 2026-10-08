using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdminSellerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AdminApprovalFiltersAndAuditsWithoutActivatingSeller()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid();
        var package = Guid.Parse("20000000-0000-0000-0000-00000000000a");
        await using var setup = data.CreateCommand("INSERT INTO identity.sellers(id,shop_name,phone,status) VALUES(@id,'Approval test','0901234567','PENDING')");
        setup.Parameters.AddWithValue("id", seller);
        await setup.ExecuteNonQueryAsync();
        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/sellers")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/flows/approve_seller", new { p_seller = seller, p_package = package })).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/admin/sellers?status=INVALID")).StatusCode);
            var list = await client.GetAsync("/api/admin/sellers?status=PENDING");
            Assert.True(list.Headers.CacheControl?.NoStore);
            var pending = await list.Content.ReadFromJsonAsync<List<AdminSellerResponse>>();
            Assert.Contains(pending!, row => row.Id == seller);
            Assert.All(pending!, row => Assert.Equal("PENDING", row.Status));
            var result = await client.PostAsJsonAsync("/api/admin/flows/approve_seller", new { p_seller = seller, p_package = package });
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var approved = await client.GetFromJsonAsync<List<AdminSellerResponse>>("/api/admin/sellers?status=APPROVED");
            var actual = Assert.Single(approved!, row => row.Id == seller);
            Assert.Equal(package, actual.PackageId);
            Assert.Null(actual.PackageExpiresAt);
            Assert.All(approved!, row => Assert.Equal("APPROVED", row.Status));
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/admin/flows/approve_seller", new { p_seller = seller, p_package = package })).StatusCode);
            await using var audit = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action='SELLER_APPROVED' AND actor_id='10000000-0000-0000-0000-000000000001'");
            audit.Parameters.AddWithValue("id", seller);
            Assert.Equal(1L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            // The isolated test database retains its append-only audit evidence.
            await using var cleanup = data.CreateCommand("DELETE FROM identity.sellers WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
