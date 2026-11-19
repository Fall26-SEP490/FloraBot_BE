using DotNetCore.CAP;
using Microsoft.AspNetCore.DataProtection;
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

        var brokerEnabled = builder.Configuration.GetValue("COMMUNICATION_BROKER_ENABLED", false);
        if (brokerEnabled)
        {
            if (!Uri.TryCreate(builder.Configuration["COMMUNICATION_API_URL"], UriKind.Absolute, out var api) ||
                api.Scheme is not ("http" or "https") || api.AbsolutePath != "/" ||
                api.UserInfo.Length != 0 || api.Query.Length != 0 || api.Fragment.Length != 0)
                throw new InvalidOperationException("COMMUNICATION_API_URL must be an HTTP(S) origin.");
            var keys = builder.Configuration["DATA_PROTECTION_KEYS_PATH"];
            if (string.IsNullOrWhiteSpace(keys))
                throw new InvalidOperationException("Persistent DATA_PROTECTION_KEYS_PATH is required for queued email.");
            builder.Services.AddDataProtection().SetApplicationName("FloraBot.Communication")
                .PersistKeysToFileSystem(new DirectoryInfo(keys));
            builder.Services.AddCap(options =>
            {
                options.DefaultGroupName = "florabot.communication." + new NpgsqlConnectionStringBuilder(connection).Database;
                options.UsePostgreSql(connection);
                options.UseRabbitMQ(rabbit =>
                {
                    rabbit.HostName = builder.Configuration["RABBITMQ_HOST"] ?? "127.0.0.1";
                    rabbit.Port = builder.Configuration.GetValue("RABBITMQ_PORT", 55672);
                    rabbit.UserName = builder.Configuration["RABBITMQ_USER"] ?? "florabot";
                    rabbit.Password = builder.Configuration["RABBITMQ_PASSWORD"]
                        ?? throw new InvalidOperationException("RABBITMQ_PASSWORD is required.");
                    rabbit.ExchangeName = builder.Configuration["RABBITMQ_EXCHANGE"]
                        ?? throw new InvalidOperationException("RABBITMQ_EXCHANGE is required for queued email.");
                    rabbit.PublishConfirms = true;
                });
            });
            builder.Services.AddScoped<EmailQueue>();
            builder.Services.AddScoped<EmailReconciliation>();
            builder.Services.AddTransient<QueuedEmailConsumer>();
            builder.Services.AddHttpClient<EmailEligibilityClient>(client => client.Timeout = TimeSpan.FromSeconds(10))
                .RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (context.Request.Path.StartsWithSegments("/internal") &&
                !CommunicationEvents.Authorized(context.Request, builder.Configuration))
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
        if (brokerEnabled)
        {
            app.MapPost("/internal/v1/email-requests", (StagedEmailRequest input, EmailQueue queue, CancellationToken ct) => queue.StageAsync(input, ct));
            app.MapPost("/internal/v1/email-requests/reconcile", (ReconcileEmailRequest input, EmailReconciliation reconciliation, CancellationToken ct) => reconciliation.ReconcileAsync(input, ct));
            app.MapGet("/internal/v1/email-requests", async (string? state, int? page, int? pageSize, NpgsqlDataSource data, CancellationToken ct) =>
            {
                var size = pageSize ?? 20;
                var number = page ?? 1;
                if (state is not (null or "UNCERTAIN" or "SENDING") || size is < 1 or > 100 || number is < 1 or > 10000)
                    return Results.BadRequest();
                await using var command = data.CreateCommand("""
                    SELECT q.external_id,d.state,q.revision,d.updated_at FROM communication.email_queue q
                    JOIN communication.email_deliveries d ON d.id=q.id
                    WHERE d.state IN ('UNCERTAIN','SENDING') AND (@state IS NULL OR d.state=@state)
                    ORDER BY d.updated_at,q.external_id LIMIT @limit OFFSET @offset
                    """);
                command.Parameters.AddWithValue("state", NpgsqlTypes.NpgsqlDbType.Text, (object?)state ?? DBNull.Value);
                command.Parameters.AddWithValue("limit", size);
                command.Parameters.AddWithValue("offset", (number - 1) * size);
                var items = new List<PendingEmailDelivery>();
                await using var rows = await command.ExecuteReaderAsync(ct);
                while (await rows.ReadAsync(ct)) items.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetFieldValue<DateTimeOffset>(3)));
                return Results.Ok(new { items, page = number, pageSize = size });
            });
        }
        app.Run();
    }
}
