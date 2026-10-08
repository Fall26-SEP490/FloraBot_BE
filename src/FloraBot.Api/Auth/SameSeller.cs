using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace FloraBot.Api.Auth;

public sealed class SameSellerRequirement : IAuthorizationRequirement;

public sealed class SameSellerHandler : AuthorizationHandler<SameSellerRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SameSellerRequirement requirement)
    {
        if (context.User.HasClaim(claim => claim.Type == "kiosk_id")) return Task.CompletedTask;
        if (context.Resource is HttpContext http && (context.User.IsInRole("ADMIN") ||
            (context.User.IsInRole("SELLER") && context.User.FindFirstValue("seller_id") == http.Request.RouteValues["sellerId"]?.ToString()))) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class TenantAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && result.AuthorizationFailure?.FailedRequirements.OfType<SameSellerRequirement>().Any() == true)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return fallback.HandleAsync(next, context, policy, result);
    }
}
