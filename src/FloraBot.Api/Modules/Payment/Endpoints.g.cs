// Generated from the supplied SQL by scripts/generate-flows.mjs.
#nullable enable
using System.Text.Json;
using FloraBot.Api.Infrastructure;

namespace FloraBot.Api.Modules.Payment;

public sealed record RequestWithdrawalRequest(
    long p_amount);

public sealed record ApproveWithdrawalRequest(
    Guid p_w);

public sealed record PayWithdrawalRequest(
    Guid p_w,
    string p_proof_url);

public sealed record RejectWithdrawalRequest(
    Guid p_w,
    string p_reason);

public sealed record AdminRefundRequest(
    Guid p_order,
    long p_amount,
    string p_reason);

public sealed record CreateRefundRequest(
    Guid p_order,
    long p_amount,
    string p_reason,
    string? p_gateway = null);

public sealed record ApproveRefundRequest(
    Guid p_refund);

public sealed record ConfirmRefundRequest(
    Guid p_refund,
    string p_proof_url);

public sealed record ReconcileGatewayRequest(
    DateOnly p_date,
    JsonElement? p_statement);

public sealed record ReconcileDailyRequest(
    DateOnly p_date);

public static class PaymentEndpoints
{
    public static void MapPayment(this WebApplication app)
    {
        app.MapPost("/api/sellers/{sellerId:guid}/flows/request_withdrawal", async (RequestWithdrawalRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("request_withdrawal", "sellerOnly", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("request_withdrawal").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Seller", "SameSeller");
        app.MapPost("/api/admin/flows/approve_withdrawal", async (ApproveWithdrawalRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("approve_withdrawal", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("approve_withdrawal").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/pay_withdrawal", async (PayWithdrawalRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("pay_withdrawal", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("pay_withdrawal").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/reject_withdrawal", async (RejectWithdrawalRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("reject_withdrawal", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("reject_withdrawal").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/admin_refund", async (AdminRefundRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("admin_refund", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("admin_refund").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/create_refund", async (CreateRefundRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("create_refund", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("create_refund").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/approve_refund", async (ApproveRefundRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("approve_refund", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("approve_refund").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/confirm_refund", async (ConfirmRefundRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("confirm_refund", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("confirm_refund").WithTags("Payment").Produces<FlowResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/reconcile_gateway", async (ReconcileGatewayRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("reconcile_gateway", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("reconcile_gateway").WithTags("Payment").Produces<GatewayReconciliationResult>().RequireAuthorization("Admin");
        app.MapPost("/api/admin/flows/reconcile_daily", async (ReconcileDailyRequest input, HttpContext http, FlowExecutor executor, CancellationToken ct) =>
            await executor.ExecuteAsync("reconcile_daily", "admin", JsonSerializer.SerializeToElement(input), http, ct))
            .WithName("reconcile_daily").WithTags("Payment").Produces<DailyReconciliationResult>().RequireAuthorization("Admin");
    }
}
