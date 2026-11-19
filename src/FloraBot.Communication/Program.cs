using System.Security.Cryptography;
using System.Text;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FloraBot.Communication;

public class CommunicationProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var token = builder.Configuration["COMMUNICATION_SERVICE_TOKEN"] ?? "";
        if (token.Length < 32 || token.Any(char.IsWhiteSpace) || token.Any(char.IsControl))
            throw new InvalidOperationException("COMMUNICATION_SERVICE_TOKEN must contain at least 32 characters without whitespace.");
        var connection = builder.Configuration["COMMUNICATION_DATABASE_URL"]
            ?? throw new InvalidOperationException("COMMUNICATION_DATABASE_URL is required for the service-owned database.");
        var settings = EmailSettings.Load(builder.Configuration, builder.Environment.IsDevelopment());
        if (settings.Provider == EmailProvider.Communication)
            throw new InvalidOperationException("Communication Service must use Brevo, GoogleSmtp or Disabled.");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1024 * 1024);
        builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = 1024 * 1024);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        builder.Services.AddSingleton<DeliveryStore>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DeliveryStore>());
        builder.Services.AddScoped<EmailDeliveryService>();
        builder.Services.AddHttpClient<TransactionalEmailSender>(client => client.Timeout = TimeSpan.FromSeconds(15))
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.TryAddTransient<ITransactionalEmailSender>(sp => sp.GetRequiredService<TransactionalEmailSender>());

        var app = builder.Build();
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (context.Request.Path.StartsWithSegments("/internal") &&
                !CryptographicOperations.FixedTimeEquals(expected,
                    Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString())))
            {
                context.Response.StatusCode = 401;
                return;
            }
            try { await next(); }
            catch (NpgsqlException)
            {
                if (context.Response.HasStarted) throw;
                await Results.Problem(statusCode: 503, detail: "Communication storage is unavailable.").ExecuteAsync(context);
            }
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "communication" }));
        app.MapGet("/ready", async (NpgsqlDataSource data, CancellationToken ct) =>
        {
            await using var command = data.CreateCommand("SELECT 1 FROM communication.email_deliveries LIMIT 1");
            await command.ExecuteScalarAsync(ct);
            return Results.Json(new
            {
                status = settings.Provider == EmailProvider.Disabled ? "disabled" : "ok",
                service = "communication"
            }, statusCode: settings.Provider == EmailProvider.Disabled ? 503 : 200);
        });
        app.MapPost("/internal/v1/email-deliveries", (EmailDeliveryRequest input,
            EmailDeliveryService delivery, CancellationToken ct) => delivery.DeliverAsync(input, ct));
        app.Run();
    }
}
