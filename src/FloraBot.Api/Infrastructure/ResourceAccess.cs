using System.Security.Claims;
using System.Text.Json;
using Npgsql;
using CatalogOwnership = FloraBot.Api.Modules.Catalog.ResourceOwnership;
using IdentityOwnership = FloraBot.Api.Modules.Identity.ResourceOwnership;
using KioskOwnership = FloraBot.Api.Modules.KioskOps.ResourceOwnership;

namespace FloraBot.Api.Infrastructure;

public static class ResourceAccess
{
    private static readonly string[] SellerResources = ["p_product", "p_slot", "p_accessory", "p_seller"];

    public static async Task<IResult?> CheckAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        ClaimsPrincipal user, string flow, Dictionary<string, JsonElement> args, CancellationToken ct)
    {
        if (user.IsInRole("SELLER") && !user.HasClaim(c => c.Type == "kiosk_id"))
        {
            foreach (var key in SellerResources)
            {
                if (!args.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                var id = value.GetGuid();
                var owner = key switch
                {
                    "p_product" => await CatalogOwnership.ProductSellerAsync(connection, transaction, id, ct),
                    "p_slot" => await KioskOwnership.SlotSellerAsync(connection, transaction, id, ct),
                    "p_accessory" => await KioskOwnership.AccessorySellerAsync(connection, transaction, id, ct),
                    "p_seller" => await IdentityOwnership.SellerAsync(connection, transaction, id, ct),
                    _ => throw new InvalidOperationException(key)
                };
                if (owner?.ToString() != user.FindFirstValue("seller_id")) return Results.NotFound();
            }
        }
        if (flow == "device_event")
        {
            var kiosk = await KioskOwnership.CommandKioskAsync(connection, transaction, args["p_cmd"].GetGuid(), ct);
            if (kiosk?.ToString() != user.FindFirstValue("kiosk_id")) return Results.Forbid();
        }
        return null;
    }
}
