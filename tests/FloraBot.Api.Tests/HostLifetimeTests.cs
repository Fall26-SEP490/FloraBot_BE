using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class HostLifetimeTests
{
    [Fact]
    public async Task StoppingHostDisposesItsDatabasePool()
    {
        await using var host = new ApiFactory();
        var source = host.Services.GetRequiredService<NpgsqlDataSource>();
        await using (var connection = await source.OpenConnectionAsync())
        {
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        }

        await host.DisposeAsync();

        // Minimal API's RunAsync and TestServer can enter disposal concurrently.
        // Factory disposal may return while the application's async cleanup is finishing.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                await using var connection = await source.OpenConnectionAsync(timeout.Token);
            }
            catch (ObjectDisposedException) { return; }
            await Task.Delay(25);
        }
        Assert.Fail("The stopped host did not dispose its database pool within five seconds.");
    }
}
