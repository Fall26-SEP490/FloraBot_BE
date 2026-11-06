using System.Security.Claims;
using FloraBot.Api.Modules.Ordering;
using Npgsql;

namespace FloraBot.Api.Modules.Payment;

public static class WebPaymentLinks
{
    public static void MapWebPaymentLinks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/member/preorders/{requestId:guid}/payment-link",
            async (Guid requestId, HttpContext http, NpgsqlDataSource source, IPayOsClientResolver resolver, ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                var logger = loggerFactory.CreateLogger("FloraBot.Api.Modules.Payment.WebPaymentLinks");
                var checkout = await WebOrders.MemberCheckoutAsync(source, requestId, Guid.Parse(http.User.FindFirstValue("sub")!), ct);
                if (checkout is null) return Results.NotFound();

                await using var connection = await source.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                http.Response.Headers.CacheControl = "no-store";
                return await SellerCheckoutPaymentLinks.CreateAsync(connection, transaction, checkout.Value, null, resolver, logger, ct);
            }).RequireAuthorization("PreorderCustomer").Produces<PaymentLinkResponse>();
    }
}
