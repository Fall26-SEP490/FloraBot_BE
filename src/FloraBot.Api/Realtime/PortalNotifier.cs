using FloraBot.Api.Modules.Identity;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace FloraBot.Api.Realtime;

public sealed class PortalNotifier(PortalConnections connections, PortalAudience audience, IHubContext<PortalHub> hub, ILogger<PortalNotifier> logger)
{
    public async Task RefreshAsync(Guid? seller)
    {
        // Notification delivery must not turn an already committed command into an HTTP failure.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            var current = connections.Snapshot().Where(c => c.ExpiresAt > DateTimeOffset.UtcNow && (c.Role == "ADMIN" || seller.HasValue && c.SellerId == seller)).ToArray();
            if (current.Length == 0) return;
            var active = await audience.ActiveAsync(current.Select(c => c.UserId).Distinct().ToArray(), timeout.Token);
            var recipients = current.Where(c => active.Any(u => u.Id == c.UserId && u.Role == c.Role && u.SellerId == c.SellerId)).Select(c => c.ConnectionId).ToArray();
            if (recipients.Length > 0) await hub.Clients.Clients(recipients).SendAsync("Refresh", cancellationToken: timeout.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or NpgsqlException or IOException)
        {
            logger.LogWarning("Portal refresh notification unavailable ({ErrorType}); clients must reload authoritative API data.", error.GetType().Name);
        }
    }
}
