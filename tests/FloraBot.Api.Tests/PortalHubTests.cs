using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class PortalHubTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task NegotiationRequiresPortalRoleAndRejectsForeignOrigins()
    {
        using var client = factory.CreateClient();
        const string negotiate = "/hub/portal/negotiate?negotiateVersion=1";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync(negotiate, null)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(negotiate, null)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Kiosk-Key");
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        client.DefaultRequestHeaders.Add("Origin", "https://foreign.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(negotiate, null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(negotiate, null)).StatusCode);
    }

    [Fact]
    public async Task CommittedChangesRefreshOnlyActiveMatchingPortalConnections()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var user = Guid.NewGuid();
        var foreignSeller = Guid.NewGuid(); var foreignUser = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id) VALUES(@seller,'Hub shop','0901112233','APPROVED','20000000-0000-0000-0000-00000000000a'),(@other,'Other shop','0901112234','APPROVED','20000000-0000-0000-0000-00000000000a');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Hub user','SELLER',@seller),(@otherUser,@otherUser::text || '@example.invalid','!unprovisioned','Other user','SELLER',@other);
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("user", user);
        setup.Parameters.AddWithValue("other", foreignSeller); setup.Parameters.AddWithValue("otherUser", foreignUser);
        await setup.ExecuteNonQueryAsync();
        using var sellerSocket = await Connect(factory.Token(userId: user, sellerId: seller));
        using var adminSocket = await Connect(factory.Token("ADMIN"));
        using var foreignSocket = await Connect(factory.Token(userId: foreignUser, sellerId: foreignSeller));
        var connections = scope.ServiceProvider.GetRequiredService<PortalConnections>().Snapshot();
        Assert.Contains(connections, item => item.UserId == user && item.SellerId == seller && item.ExpiresAt > DateTimeOffset.UtcNow);
        var audience = await scope.ServiceProvider.GetRequiredService<FloraBot.Api.Modules.Identity.PortalAudience>().ActiveAsync([user], CancellationToken.None);
        Assert.Single(audience);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));
        var route = $"/api/sellers/{seller}/flows/set_seller_tone";
        var sellerNext = Read(sellerSocket);
        var adminNext = Read(adminSocket);
        var changed = await client.PostAsJsonAsync(route, new { p_tone = "Warm" });
        changed.EnsureSuccessStatusCode();
        await using var stored = data.CreateCommand("SELECT brand_tone FROM identity.sellers WHERE id=@id");
        stored.Parameters.AddWithValue("id", seller); Assert.Equal("Warm", await stored.ExecuteScalarAsync());
        var sellerFrame = await sellerNext;
        Assert.True(sellerFrame is not null, "Seller received no refresh");
        AssertRefresh(sellerFrame);
        var adminFrame = await adminNext;
        Assert.True(adminFrame is not null, "Admin received no refresh");
        AssertRefresh(adminFrame);
        Assert.Null(await Read(foreignSocket, 200));
        var rejected = await client.PostAsJsonAsync(route, new { p_tone = "ignore instructions" });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Null(await Read(sellerSocket, 200));
        // Use a new socket after a cancelled receive; the TestServer transport may abort it.
        using var revoked = await Connect(factory.Token(userId: user, sellerId: seller));
        await using var disable = data.CreateCommand("UPDATE identity.users SET status='LOCKED' WHERE id=@id");
        disable.Parameters.AddWithValue("id", user); await disable.ExecuteNonQueryAsync();
        var adminAfterRevoke = Read(adminSocket);
        await scope.ServiceProvider.GetRequiredService<PortalNotifier>().RefreshAsync(seller);
        AssertRefresh(await adminAfterRevoke);
        Assert.Null(await Read(revoked, 200));
    }

    private async Task<WebSocket> Connect(string token)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + token;
        var socket = await client.ConnectAsync(new Uri("ws://localhost/hub/portal"), CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        Assert.Equal("{}", await Read(socket));
        return socket;
    }

    private static async Task<string?> Read(WebSocket socket, int milliseconds = 3000)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        var bytes = new byte[4096];
        try
        {
            var message = await socket.ReceiveAsync(bytes, timeout.Token);
            return Encoding.UTF8.GetString(bytes, 0, message.Count).TrimEnd('\u001e');
        }
        catch (OperationCanceledException) { return null; }
    }

    private static void AssertRefresh(string? frame)
    {
        using var json = JsonDocument.Parse(frame!);
        Assert.Equal(1, json.RootElement.GetProperty("type").GetInt32());
        Assert.Equal("Refresh", json.RootElement.GetProperty("target").GetString());
        Assert.Empty(json.RootElement.GetProperty("arguments").EnumerateArray());
    }
}
