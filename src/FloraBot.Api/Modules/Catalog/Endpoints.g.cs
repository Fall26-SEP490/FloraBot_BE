// Generated from the supplied SQL by scripts/generate-flows.mjs.
#nullable enable
using System.Text.Json;
using FloraBot.Api.Infrastructure;

namespace FloraBot.Api.Modules.Catalog;

public sealed record SetProductStatusRequest(
    Guid p_product,
    string p_status);

public sealed record UpdateProductPriceRequest(
    Guid p_product,
    long p_price);

public sealed record AddProductPhotoRequest(
    Guid p_product,
    string p_url);

public sealed record RestockAccessoryRequest(
    Guid p_accessory,
    int p_qty);

public static class CatalogEndpoints
{
    public static void MapCatalog(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/flows/set_product_status", async (SetProductStatusRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("set_product_status", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("set_product_status").WithTags("Catalog").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/update_product_price", async (UpdateProductPriceRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("update_product_price", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("update_product_price").WithTags("Catalog").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/add_product_photo", async (AddProductPhotoRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("add_product_photo", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("add_product_photo").WithTags("Catalog").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/restock_accessory", async (RestockAccessoryRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("restock_accessory", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("restock_accessory").WithTags("Catalog").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
    }
}
