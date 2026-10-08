using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using Npgsql;

namespace FloraBot.Api.ReadModels;

public static class ScreenReadEndpoints
{
    public static void MapScreenReads(this WebApplication app)
    {
        foreach (var view in new[] { "v_admin_queue", "v_refund_queue", "v_revenue_daily", "v_platform_revenue", "v_ai_effectiveness" })
        {
            var safeView = view;
            app.MapGet("/api/admin/reports/" + view, async (NpgsqlDataSource data, CancellationToken ct) =>
                Results.Ok(await QueryRows(data, $"SELECT row_to_json(r) FROM (SELECT * FROM screen.{safeView} LIMIT 100) r", null, ct))).RequireAuthorization("Admin");
        }
        app.MapGet("/api/kiosks/{kioskId:guid}/catalog", async (Guid kioskId, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            return Results.Ok(await QueryRows(data, "SELECT row_to_json(r) FROM screen.v_kiosk_catalog r WHERE kiosk_id=@id", kioskId, ct));
        }).RequireAuthorization("Shopping");
        app.MapGet("/api/kiosks/{kioskId:guid}/catalog/items", async (Guid kioskId, HttpContext http, NpgsqlDataSource data, CancellationToken ct) =>
        {
            if (http.User.FindFirstValue("kiosk_id") != kioskId.ToString()) return Results.Forbid();
            await using var command = data.CreateCommand("SELECT bouquet_id,name,gia,slot_code,shop_name FROM screen.v_kiosk_catalog WHERE kiosk_id=@id ORDER BY slot_code");
            command.Parameters.AddWithValue("id", kioskId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var items = new List<KioskCatalogResponse>();
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)));
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(items);
        }).RequireAuthorization("Shopping").Produces<List<KioskCatalogResponse>>();
    }
    private static async Task<List<JsonElement>> QueryRows(NpgsqlDataSource data, string sql, Guid? id, CancellationToken ct)
    {
        await using var command = data.CreateCommand(sql);
        if (id is not null) command.Parameters.AddWithValue("id", id.Value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<JsonElement>();
        while (await reader.ReadAsync(ct)) rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return rows;
    }
}
