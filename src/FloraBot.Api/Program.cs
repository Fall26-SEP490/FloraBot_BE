using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Jobs;
using FloraBot.Api.Realtime;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Catalog;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.Modules.Payment;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Ai;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using StackExchange.Redis;
using DotNetCore.CAP;

if (args.FirstOrDefault() == "provision-user")
{
    if (!Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Use scripts/provision-user.ps1 to enter the password securely, or redirect standard input.");
        Environment.ExitCode = 2;
        return;
    }
    Environment.ExitCode = await PortalProvisioning.RunAsync(args, new ConfigurationBuilder().AddEnvironmentVariables().Build(), Console.In, Console.Out);
    return;
}
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["JWT_SIGNING_KEY"]) && builder.Environment.IsDevelopment())
    builder.Configuration["JWT_SIGNING_KEY"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
var signingKey = builder.Configuration["JWT_SIGNING_KEY"] ?? throw new InvalidOperationException("JWT_SIGNING_KEY is required.");
if (Encoding.UTF8.GetByteCount(signingKey) < 32) throw new InvalidOperationException("JWT_SIGNING_KEY must contain at least 32 bytes.");
var connectionString = builder.Configuration["DATABASE_URL"] ?? throw new InvalidOperationException("DATABASE_URL is required.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddCap(options =>
{
    options.DefaultGroupName = "florabot." + new NpgsqlConnectionStringBuilder(connectionString).Database;
    options.UsePostgreSql(connectionString);
    options.UseRabbitMQ(rabbit =>
    {
        rabbit.HostName = builder.Configuration["RABBITMQ_HOST"] ?? "127.0.0.1";
        rabbit.Port = builder.Configuration.GetValue("RABBITMQ_PORT", 55672);
        rabbit.UserName = builder.Configuration["RABBITMQ_USER"] ?? "florabot";
        rabbit.Password = builder.Configuration["RABBITMQ_PASSWORD"] ??
            (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
                ? "local-florabot-only" : throw new InvalidOperationException("RABBITMQ_PASSWORD is required."));
        rabbit.PublishConfirms = true;
        rabbit.ExchangeName = "florabot." + new NpgsqlConnectionStringBuilder(connectionString).Database;
    });
});
builder.Services.AddTransient<PaymentEventSubscriber>();
builder.Services.AddTransient<SubscriptionEventSubscriber>();
builder.Services.AddTransient<SellerEmailSubscriber>();
builder.Services.AddScoped<SellerEmailReader>();
builder.Services.AddScoped<EmailDelivery>();
builder.Services.AddTransient<OrderEventSubscriber>();
builder.Services.AddTransient<RefundEventSubscriber>();
if (builder.Configuration.GetValue("MQTT_ENABLED", false))
{
    builder.Services.AddSingleton(DeviceKeyRing.Load(builder.Configuration["MQTT_CREDENTIALS_FILE"]
        ?? throw new InvalidOperationException("MQTT_CREDENTIALS_FILE is required.")));
    builder.Services.AddScoped<DeviceEventInbox>();
    builder.Services.AddScoped<DeviceCommandDispatcher>();
    builder.Services.AddHostedService<MqttEventWorker>();
}
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp =>
{
    var db = new FloraDbContext(new DbContextOptionsBuilder<FloraDbContext>().UseNpgsql(connectionString).Options);
    db.HttpContextAccessor = sp.GetRequiredService<IHttpContextAccessor>();
    return db;
});
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(builder.Configuration["VALKEY_URL"] ?? "localhost:56379"));
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<OtpService>();
builder.Services.AddSingleton<IAuthorizationHandler, SameSellerHandler>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, TenantAuthorizationResultHandler>();
builder.Services.AddHttpClient("sms", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddHttpClient<PayOsClient>(client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<FlowExecutor>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(EmailSettings.Load(builder.Configuration, builder.Environment.IsDevelopment()));
builder.Services.AddHttpClient<TransactionalEmailSender>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<CloudinaryMedia>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .RemoveAllLoggers()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<PrivateEvidence>();
builder.Services.AddHttpClient<AdvisorClient>(client => client.Timeout = TimeSpan.FromMilliseconds(1800))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<AiAdvisor>();
builder.Services.AddScoped<AdvisorCache>();
builder.Services.AddSignalR(options => { options.MaximumReceiveMessageSize = 4096; options.EnableDetailedErrors = false; });
builder.Services.AddSingleton<PortalConnections>();
builder.Services.AddScoped<PortalAudience>();
builder.Services.AddScoped<PortalNotifier>();
builder.Services.AddSingleton<JobRunner>();
if (builder.Configuration.GetValue("JOBS_ENABLED", !builder.Environment.IsEnvironment("Testing")))
    builder.Services.AddHostedService<FloraJobs>();
var protection = builder.Services.AddDataProtection();
if (!string.IsNullOrWhiteSpace(builder.Configuration["DATA_PROTECTION_KEYS_PATH"]))
    protection.PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DATA_PROTECTION_KEYS_PATH"]!));
builder.Services.AddProblemDetails();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
builder.Services.AddOpenApi();
builder.Services.AddAuthentication("selector")
    .AddPolicyScheme("selector", "Device or session", options => options.ForwardDefaultSelector = http =>
        http.Request.Headers.ContainsKey("X-Kiosk-Key") && !http.Request.Headers.ContainsKey("Authorization") ? "kiosk" : "bearer")
    .AddScheme<AuthenticationSchemeOptions, KioskAuthentication>("kiosk", _ => { })
    .AddJwtBearer("bearer", options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateIssuer = true,
            ValidIssuer = TokenService.Issuer,
            ValidateAudience = true,
            ValidAudience = TokenService.Issuer,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            RoleClaimType = "role",
            NameClaimType = "name"
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context => { if (!context.Request.Headers.ContainsKey("Authorization")) context.Token = context.Request.Cookies["florabot_access"]; return Task.CompletedTask; },
            OnTokenValidated = async context =>
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<FloraDbContext>();
                if (!Guid.TryParse(context.Principal?.FindFirstValue("sub"), out var id)) { context.Fail("Invalid session."); return; }
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == "ACTIVE");
                if (user is null || user.Role != context.Principal?.FindFirstValue("role") || user.SellerId?.ToString() != context.Principal?.FindFirstValue("seller_id")) context.Fail("Session revoked.");
                else if (context.Principal?.FindFirstValue("credential_version") is { } version && version != TokenService.CredentialVersion(user)) context.Fail("Credentials changed.");
            }
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", p => p.RequireRole("ADMIN"))
    .AddPolicy("Seller", p => p.RequireRole("SELLER").RequireAssertion(c => !c.User.HasClaim(claim => claim.Type == "kiosk_id")))
    .AddPolicy("SameSeller", p => p.RequireAuthenticatedUser().AddRequirements(new SameSellerRequirement()))
    .AddPolicy("Merchant", p => p.RequireRole("ADMIN", "SELLER").RequireAssertion(c => !c.User.HasClaim(claim => claim.Type == "kiosk_id")))
    .AddPolicy("Portal", p => p.RequireRole("ADMIN", "SELLER").RequireAssertion(c => !c.User.HasClaim(claim => claim.Type == "kiosk_id")))
    .AddPolicy("Account", p => p.RequireRole("ADMIN", "STAFF", "SELLER", "CUSTOMER").RequireAssertion(c => !c.User.HasClaim(claim => claim.Type == "kiosk_id")))
    .AddPolicy("Staff", p => p.RequireRole("STAFF").RequireAssertion(c => !c.User.HasClaim(claim => claim.Type == "kiosk_id")))
    .AddPolicy("MemberIdentity", p => p.RequireRole("CUSTOMER", "SELLER").RequireAssertion(c => !c.User.HasClaim(claim => claim.Type == "kiosk_id")))
    .AddPolicy("Member", p => p.RequireRole("CUSTOMER", "SELLER").RequireAssertion(c => !c.User.HasClaim(claim => claim.Type == "kiosk_id")))
    .AddPolicy("Kiosk", p => p.RequireRole("KIOSK"))
    .AddPolicy("Customer", p => p.RequireRole("CUSTOMER").RequireClaim("kiosk_id"))
    .AddPolicy("KioskMember", p => p.RequireRole("CUSTOMER", "SELLER").RequireClaim("kiosk_id"))
    .AddPolicy("Shopping", p => p.RequireRole("KIOSK", "CUSTOMER", "SELLER").RequireClaim("kiosk_id"));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("advisor", http => RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirstValue("kiosk_id") ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    foreach (var name in new[] { "auth", "receipt" })
        options.AddPolicy(name, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = name == "auth" ? 10 : 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var origins = (builder.Configuration["ALLOWED_ORIGINS"] ?? (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")
    ? "http://localhost:4321,http://localhost:5173,http://localhost:5174,http://127.0.0.1:4321,http://127.0.0.1:5173,http://127.0.0.1:5174"
    : "")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
var app = builder.Build();
app.Use(async (http, next) =>
{
    // A 5,000-row statement can exceed the default 64 KiB even after field normalization.
    if (string.Equals(http.Request.Path.Value?.TrimEnd('/'), "/api/admin/flows/reconcile_gateway", StringComparison.OrdinalIgnoreCase) &&
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        bodyLimit.MaxRequestBodySize = 8 * 1024 * 1024;
    http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (http.Request.Path.StartsWithSegments("/api/auth") || http.Request.Path.StartsWithSegments("/api/member") || http.Request.Path.StartsWithSegments("/api/kiosks") || http.Request.Path.StartsWithSegments("/api/receipts") || http.Request.Path.StartsWithSegments("/api/admin/refunds") ||
        http.Request.Path.StartsWithSegments("/api/admin/flows/reconcile_gateway") || http.Request.Path.StartsWithSegments("/api/admin/flows/reconcile_daily"))
        http.Response.Headers.CacheControl = "no-store";
    if (http.Request.Path.StartsWithSegments("/hub") && http.Request.Headers.Origin.Count > 0)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (origin != $"{http.Request.Scheme}://{http.Request.Host}" && !origins.Contains(origin))
        {
            http.Response.StatusCode = 403;
            return;
        }
    }
    if (http.Request.Method is not ("GET" or "HEAD" or "OPTIONS") &&
        (http.Request.Cookies.ContainsKey("florabot_access") || http.Request.Cookies.ContainsKey("florabot_refresh")))
    {
        var origin = http.Request.Headers.Origin.ToString();
        var sameOrigin = $"{http.Request.Scheme}://{http.Request.Host}";
        if (origin != sameOrigin && !origins.Contains(origin)) { http.Response.StatusCode = 403; return; }
    }
    try { await next(); }
    catch (PostgresException ex)
    {
        var status = ex.SqlState switch { "P0001" => 409, "23505" => 409, "23503" or "23514" or "22P02" or "22023" => 400, _ => 500 };
        var detail = ex.SqlState == "P0001" ? ex.MessageText : status == 500 ? "Không thể xử lý yêu cầu lúc này." : "Dữ liệu không hợp lệ hoặc bị trùng.";
        await Results.Problem(statusCode: status, title: "Yêu cầu chưa được thực hiện", detail: detail).ExecuteAsync(http);
    }
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHub<PortalHub>("/hub/portal", options => options.CloseOnAuthenticationExpiration = true).RequireAuthorization("Portal");
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapOpenApi().RequireAuthorization("Admin");
app.MapAuth();
app.MapMembers();
app.MapWebOrders();
app.MapWebPaymentLinks();
FloraBot.Api.ReadModels.WebCatalog.MapWebCatalog(app);
app.MapOtp();
app.MapReads();
app.MapWalletRead();
app.MapSellerBankRead();
app.MapAdminIncidentsRead();
app.MapStaffTasks();
app.MapStaffStock();
app.MapStaffTaskDetails();
app.MapStaffEvidence();
app.MapStaffIncidentResolution();
app.MapAdminComplaintsRead();
app.MapAdminWithdrawalsRead();
app.MapAdminRefundsRead();
app.MapReceiptRead();
app.MapCustomerHistory();
app.MapCustomerPoints();
app.MapAccessoryCatalog();
app.MapSellerAccessories();
app.MapProductPhotos();
app.MapProducts();
PrivateEvidence.MapPrivateEvidence(app);
app.MapRegistration();
app.MapPayOs();
app.MapPaymentLinks();
app.MapKioskPaymentLinks();
AiAdvisor.MapReads(app);
app.MapIdentity(); app.MapCatalog(); app.MapKioskOps(); app.MapOrdering(); app.MapPayment(); app.MapNotify(); app.MapAi();
// CAP bootstraps in the background; accept requests only after its outbox tables exist.
// Serialize first-time DDL across processes sharing a database.
await using (var initialization = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
await using (var initializationLock = await initialization.BeginTransactionAsync())
{
    await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('florabot:cap-initialize',0))", initialization, initializationLock);
    await command.ExecuteNonQueryAsync();
    await app.Services.GetRequiredService<DotNetCore.CAP.Persistence.IStorageInitializer>().InitializeAsync(CancellationToken.None);
    await initializationLock.CommitAsync();
}
app.Run();
public partial class Program { }
