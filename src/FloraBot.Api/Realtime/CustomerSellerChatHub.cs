using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FloraBot.Api.Realtime;

/// <summary>
/// Authenticated notification channel for customer-preorder and seller chat messages.
/// Messages are addressed to the counterpart's user ID by CustomerSellerChatNotifier;
/// clients do not choose or join arbitrary chat groups.
/// </summary>
[Authorize(Policy = "MemberIdentity")]
public sealed class CustomerSellerChatHub : Hub
{
}

/// <summary>
/// SignalR's default user identifier does not use JWT's raw "sub" claim when inbound
/// claim mapping is disabled. Use the same stable identity as the REST authorization.
/// </summary>
public sealed class JwtSubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst("sub")?.Value;
}

public sealed record CustomerSellerChatMessageReceived(
    Guid ChatId,
    Guid Id,
    string SenderName,
    string SenderRole,
    string? Content,
    DateTimeOffset CreatedAt,
    string? ImageUrl,
    long UnreadCount);

public sealed record CustomerSellerChatMessagesRead(
    Guid ChatId,
    string ReaderName,
    string ReaderRole,
    Guid ReadThroughMessageId,
    DateTimeOffset ReadThroughCreatedAt,
    DateTimeOffset ReadAt,
    long MessagesMarkedRead);

/// <summary>
/// Best-effort real-time delivery. The database commit remains authoritative if a
/// recipient is offline or SignalR is temporarily unavailable.
/// </summary>
public sealed class CustomerSellerChatNotifier(
    IHubContext<CustomerSellerChatHub> hub,
    ILogger<CustomerSellerChatNotifier> logger)
{
    public async Task MessageReceivedAsync(
        IReadOnlyCollection<Guid> recipientUserIds,
        CustomerSellerChatMessageReceived message)
    {
        var recipients = recipientUserIds
            .Where(id => id != Guid.Empty)
            .Select(id => id.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (recipients.Length == 0) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await hub.Clients.Users(recipients)
                .SendAsync("ChatMessageReceived", message, timeout.Token);
        }
        catch (Exception error)
        {
            // The message is already committed. Returning an error could cause the
            // sender to retry and create a duplicate message.
            logger.LogWarning("Chat SignalR notification unavailable ({ErrorType}) for chat {ChatId}.",
                error.GetType().Name, message.ChatId);
        }
    }

    public async Task MessagesReadAsync(
        IReadOnlyCollection<Guid> recipientUserIds,
        CustomerSellerChatMessagesRead message)
    {
        var recipients = recipientUserIds
            .Where(id => id != Guid.Empty)
            .Select(id => id.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (recipients.Length == 0) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await hub.Clients.Users(recipients)
                .SendAsync("ChatMessagesRead", message, timeout.Token);
        }
        catch (Exception error)
        {
            logger.LogWarning("Chat SignalR read receipt unavailable ({ErrorType}) for chat {ChatId}.",
                error.GetType().Name, message.ChatId);
        }
    }
}
