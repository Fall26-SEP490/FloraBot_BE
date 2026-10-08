using System.Text.Json;
using Npgsql;

namespace FloraBot.Api.Modules.Ordering;

public static class ReceiptAccess
{
    public static string? NormalizeToken(string? value)
    {
        var token = value?.Trim().ToUpperInvariant();
        return token is { Length: 8 } && token.All(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c)) ? token : null;
    }

    public static async Task<IResult?> CheckAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Dictionary<string, JsonElement> args, CancellationToken ct)
    {
        var token = args.TryGetValue("p_tracking", out var tracking) && tracking.ValueKind == JsonValueKind.String ? NormalizeToken(tracking.GetString()) : null;
        if (token is null || !args.TryGetValue("p_order", out var order) || order.ValueKind != JsonValueKind.String || !order.TryGetGuid(out var id) || id == Guid.Empty)
            return Results.NotFound();
        await using var command = new NpgsqlCommand("SELECT id FROM ordering.orders WHERE id=@id AND tracking_token=@token FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", id); command.Parameters.AddWithValue("token", token);
        if (await command.ExecuteScalarAsync(ct) is null) return Results.NotFound();
        args["p_tracking"] = JsonSerializer.SerializeToElement(token);
        return null;
    }
}
