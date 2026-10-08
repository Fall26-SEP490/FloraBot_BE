using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.BrowserHost;

public static class BrowserHost
{
    public static async Task Main()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DATABASE_URL"));
        if (connection.Database != "florabot_browser_tests" || connection.Host is not ("localhost" or "127.0.0.1"))
            throw new InvalidOperationException("Browser host requires the isolated local browser-test database.");
        using var transport = new HttpClient(new MediaTransport());
        var mediaSettings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CLOUDINARY_CLOUD_NAME"] = "browser-fixture",
            ["CLOUDINARY_API_KEY"] = "fixture",
            ["CLOUDINARY_API_SECRET"] = "browser-fixture-secret"
        }).Build();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/FloraBot.Api")));
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services => services.AddScoped(_ => new CloudinaryMedia(transport, mediaSettings, TimeProvider.System)));
        });
        factory.UseKestrel(options => options.ListenLocalhost(5081));
        factory.StartServer();
        Console.WriteLine("Browser test API ready; Cloudinary transport is simulated, database and API are real.");
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}
