// Generated from the supplied SQL by scripts/generate-flows.mjs.
#nullable enable
using System.Text.Json;
using FloraBot.Api.Infrastructure;

namespace FloraBot.Api.Modules.Identity;

public sealed record SetSellerBankRequest(
    string p_bank,
    string p_account_enc,
    string p_holder);

public sealed record SetSellerToneRequest(
    string p_tone);

public sealed record SubscribeRequest(
    int p_months,
    DateOnly? p_from = null);

public sealed record ApproveSellerRequest(
    Guid p_seller,
    Guid p_package,
    string? p_tone = null);

public sealed record SetSellerStatusRequest(
    Guid p_seller,
    string p_status);

public sealed record ForgetCustomerRequest(
);

public static class IdentityEndpoints
{
    public static void MapIdentity(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/flows/set_seller_bank", async (SetSellerBankRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("set_seller_bank", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("set_seller_bank").WithTags("Identity").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/set_seller_tone", async (SetSellerToneRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("set_seller_tone", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("set_seller_tone").WithTags("Identity").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/sellers/{sellerId:guid}/flows/subscribe", async (SubscribeRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("subscribe", "seller", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("subscribe").WithTags("Identity").Produces<FlowResult>().RequireAuthorization("Merchant", "SameSeller");
        app.MapPost("/api/admin/flows/approve_seller", async (ApproveSellerRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("approve_seller", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("approve_seller").WithTags("Identity").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/set_seller_status", async (SetSellerStatusRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("set_seller_status", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("set_seller_status").WithTags("Identity").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/kiosks/{kioskId:guid}/flows/forget_customer", async (ForgetCustomerRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("forget_customer", "customer", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("forget_customer").WithTags("Identity").Produces<FlowResult>().RequireAuthorization("Customer");
    }
}
