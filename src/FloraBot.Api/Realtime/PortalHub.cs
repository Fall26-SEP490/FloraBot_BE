using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FloraBot.Api.Realtime;

public sealed record PortalConnection(string ConnectionId, Guid UserId, string Role, Guid? SellerId, DateTimeOffset ExpiresAt);

public sealed class PortalConnections
{
    private readonly ConcurrentDictionary<string, PortalConnection> connections = new();
    public PortalConnection[] Snapshot() => connections.Values.ToArray();
    public void Add(PortalConnection connection) => connections[connection.ConnectionId] = connection;
    public void Remove(string id) => connections.TryRemove(id, out _);
}

[Authorize(Policy = "Portal")]
public sealed class PortalHub(PortalConnections connections) : Hub
{
    public override Task OnConnectedAsync()
    {
        var user = Context.User!;
        if (!Guid.TryParse(user.FindFirstValue("sub"), out var id) || !long.TryParse(user.FindFirstValue("exp"), out var expiry))
        {
            Context.Abort();
            return Task.CompletedTask;
        }
        Guid? seller = Guid.TryParse(user.FindFirstValue("seller_id"), out var sellerId) ? sellerId : null;
        connections.Add(new(Context.ConnectionId, id, user.FindFirstValue("role")!, seller, DateTimeOffset.FromUnixTimeSeconds(expiry)));
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}
