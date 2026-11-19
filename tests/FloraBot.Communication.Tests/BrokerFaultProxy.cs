using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace FloraBot.Communication.Tests;

// A real TCP cut/reconnect in front of the local AMQP broker; no container restart or production access.
internal sealed class BrokerFaultProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    private readonly string targetHost;
    private readonly int targetPort;
    private readonly Task pump;
    private volatile bool online = true;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public BrokerFaultProxy(string host, int port)
    {
        targetHost = host; targetPort = port;
        listener.Start();
        pump = AcceptAsync();
    }

    public void SetOnline(bool value)
    {
        online = value;
        if (!value) foreach (var client in clients.Keys) client.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!online) { client.Dispose(); continue; }
                clients.TryAdd(client, 0);
                _ = ForwardAsync(client);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        using var upstream = new TcpClient();
        clients.TryAdd(upstream, 0);
        try
        {
            await upstream.ConnectAsync(targetHost, targetPort, stop.Token);
            if (!online) return;
            var down = client.GetStream(); var up = upstream.GetStream();
            await Task.WhenAny(down.CopyToAsync(up, stop.Token), up.CopyToAsync(down, stop.Token));
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        finally
        {
            client.Dispose();
            clients.TryRemove(client, out _); clients.TryRemove(upstream, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); SetOnline(false);
        await pump;
        stop.Dispose();
    }
}
