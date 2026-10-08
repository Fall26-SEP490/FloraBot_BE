using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

internal static class EvidenceFixture
{
    internal static string Admin(IServiceProvider services, string purpose, Guid resource, Guid actor) =>
        Reference(services, purpose, PrivateEvidence.AdminBinding(actor.ToString(), resource));
    internal static string Reference(IServiceProvider services, string purpose, string binding)
    {
        using var scope = services.CreateScope();
        var id = $"florabot-evidence/{Guid.NewGuid():N}";
        return scope.ServiceProvider.GetRequiredService<PrivateEvidence>().Issue(purpose, binding,
            new VerifiedMedia($"https://res.cloudinary.com/test-cloud/image/authenticated/v1/{id}.png", id, "image/png", 1, "fixture")).Reference;
    }
}
