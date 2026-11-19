using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FloraBot.Api.Modules.Identity;
using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public static class CommunicationEndpoints
{
    public static void MapCommunication(this WebApplication app)
    {
        app.MapPost("/internal/v1/communication/email-eligibility", async (EmailEligibilityRequest input,
            HttpContext http, IConfiguration config, NpgsqlDataSource data, SellerEmailReader reader, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!CommunicationEvents.Authorized(http.Request, config)) return Results.Unauthorized();
            await using var query = data.CreateCommand("""
                SELECT d.seller_id,d.recipient_user_id,d.kind,d.subscription_id,u.status,u.role,u.seller_id
                FROM notify.communication_deliveries d LEFT JOIN identity.users u ON u.id=d.recipient_user_id
                WHERE d.delivery_id=@id
                """);
            query.Parameters.AddWithValue("id", input.DeliveryId);
            SellerEmailRequested business;
            Guid recipient;
            await using (var rows = await query.ExecuteReaderAsync(ct))
            {
                if (!await rows.ReadAsync(ct)) return Results.Ok(new EmailEligibilityResponse(false));
                var seller = rows.GetGuid(0);
                recipient = rows.GetGuid(1);
                business = new(seller, rows.GetString(2), rows.IsDBNull(3) ? null : rows.GetGuid(3));
                if (rows.IsDBNull(4) || rows.GetString(4) != "ACTIVE" || rows.GetString(5) != "SELLER" ||
                    rows.IsDBNull(6) || rows.GetGuid(6) != seller) return Results.Ok(new EmailEligibilityResponse(false));
            }
            try
            {
                var content = await reader.ReadAsync(business, ct);
                var target = content?.Recipients.SingleOrDefault(row => row.UserId == recipient);
                return Results.Ok(new EmailEligibilityResponse(content is not null && target is not null &&
                    CommunicationEvents.Fingerprint(new(target.Email, content.Subject, content.Text)) == input.Fingerprint));
            }
            catch (InvalidOperationException) { return Results.Problem(statusCode: 503, detail: "Business eligibility is temporarily unavailable."); }
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapGet("/api/admin/communication/email-deliveries", async (string? state, int? page, int? pageSize,
            HttpContext http, IHttpClientFactory clients, EmailSettings settings, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if ((state ?? "UNCERTAIN") is not ("UNCERTAIN" or "SENDING") || page is < 1 || pageSize is < 1 or > 100)
                return Results.BadRequest();
            return await ForwardAsync(clients, settings, HttpMethod.Get,
                $"/internal/v1/email-requests?state={state ?? "UNCERTAIN"}&page={page ?? 1}&pageSize={pageSize ?? 25}", null, ct);
        }).RequireAuthorization("Admin");

        app.MapPost("/api/admin/communication/email-deliveries/reconcile", async (ReconcileEmailRequest input,
            HttpContext http, IHttpClientFactory clients, EmailSettings settings, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            return await ForwardAsync(clients, settings, HttpMethod.Post, "/internal/v1/email-requests/reconcile",
                input with { ActorId = actor }, ct);
        }).RequireAuthorization("Admin");
    }

    private static async Task<IResult> ForwardAsync(IHttpClientFactory clients, EmailSettings settings,
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (settings.ServiceUrl is null || settings.Provider != EmailProvider.Communication)
            return Results.Problem(statusCode: 503, detail: "Communication Service is not configured.");
        using var request = new HttpRequestMessage(method, new Uri(settings.ServiceUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await clients.CreateClient("communication-control").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return Results.Problem(statusCode: (int)response.StatusCode, detail: "Reconciliation could not be performed. Check the current delivery state and operation ID.");
            await response.Content.LoadIntoBufferAsync(65536, ct);
            return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json", statusCode: (int)response.StatusCode);
        }
        catch (Exception error) when (error is HttpRequestException or IOException || error is OperationCanceledException && !ct.IsCancellationRequested)
        { return Results.Problem(statusCode: 503, detail: "Communication Service is unavailable."); }
    }
}
