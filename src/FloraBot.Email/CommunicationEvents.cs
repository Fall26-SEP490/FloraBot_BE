using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Modules.Notify;

public sealed record StagedEmailRequest(string DeliveryId, TransactionalEmail Message);
public sealed record QueuedEmailDelivery(string DeliveryId, string Signature);
public sealed record EmailDeliveryOutcome(string DeliveryId, string State, long Revision, string Signature);
public sealed record EmailEligibilityRequest(string DeliveryId, string Fingerprint);
public sealed record EmailEligibilityResponse(bool Eligible);
public sealed record ReconcileEmailRequest(Guid OperationId, string DeliveryId, string ExpectedState,
    long ExpectedRevision, string Decision, string ProviderReference, string Reason, Guid ActorId);
public sealed record PendingEmailDelivery(string DeliveryId, string State, long Revision, DateTimeOffset UpdatedAt);

public static class CommunicationEvents
{
    public const string Requested = "florabot.communication.email.requested.v1";
    public const string Outcome = "florabot.communication.email.outcome.v1";
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Fingerprint(TransactionalEmail message) => Hash(JsonSerializer.Serialize(message with { DeliveryId = null }));
    public static string SignRequest(string id, string token) => Sign("request:v1:" + id, token);
    public static string SignOutcome(string id, string state, long revision, string token) =>
        Sign(FormattableString.Invariant($"outcome:v1:{id}:{state}:{revision}"), token);

    public static bool ValidRequest(QueuedEmailDelivery message, IConfiguration config) =>
        Tokens(config).Any(token => Equal(message.Signature, SignRequest(message.DeliveryId, token)));
    public static bool ValidOutcome(EmailDeliveryOutcome message, IConfiguration config) =>
        Tokens(config).Any(token => Equal(message.Signature, SignOutcome(message.DeliveryId, message.State, message.Revision, token)));
    public static bool Authorized(HttpRequest request, IConfiguration config) =>
        Tokens(config).Any(token => Equal(request.Headers.Authorization.ToString(), "Bearer " + token));

    private static IEnumerable<string> Tokens(IConfiguration config) =>
        new[] { config["COMMUNICATION_SERVICE_TOKEN"], config["COMMUNICATION_PREVIOUS_SERVICE_TOKEN"] }
            .Where(token => token?.Length >= 32).Select(token => token!);
    private static string Sign(string value, string token) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(value)));
    private static bool Equal(string? left, string right) => left is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}
