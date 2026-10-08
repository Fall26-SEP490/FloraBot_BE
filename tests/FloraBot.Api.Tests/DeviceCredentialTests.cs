using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class DeviceCredentialTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task RotationAndDisableRevokeDeviceAccessOnTheNextRequest()
    {
        using var scope = factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var id = Guid.NewGuid();
        var original = Guid.NewGuid().ToString("N");
        var replacement = Guid.NewGuid().ToString("N");
        static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        await using var insert = source.CreateCommand("""
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash)
            VALUES(@id,@code,'Credential fixture','Test address','HCM',@code,@code,'ONLINE',@hash)
            """);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("code", id.ToString());
        insert.Parameters.AddWithValue("hash", Hash(original));
        await insert.ExecuteNonQueryAsync();
        try
        {
            using var client = factory.CreateClient();
            async Task<HttpStatusCode> Read(string key)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/kiosks/{id}/catalog/items");
                request.Headers.Add("X-Kiosk-Key", key);
                using var response = await client.SendAsync(request);
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.OK, await Read(original));
            await using var rotate = source.CreateCommand("UPDATE kiosk_ops.kiosks SET api_key_hash=@hash WHERE id=@id");
            rotate.Parameters.AddWithValue("hash", Hash(replacement));
            rotate.Parameters.AddWithValue("id", id);
            await rotate.ExecuteNonQueryAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, await Read(original));
            Assert.Equal(HttpStatusCode.OK, await Read(replacement));
            await using var disable = source.CreateCommand("UPDATE kiosk_ops.kiosks SET status='DISABLED' WHERE id=@id");
            disable.Parameters.AddWithValue("id", id);
            await disable.ExecuteNonQueryAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, await Read(replacement));
        }
        finally
        {
            await using var cleanup = source.CreateCommand("DELETE FROM kiosk_ops.kiosks WHERE id=@id");
            cleanup.Parameters.AddWithValue("id", id);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
