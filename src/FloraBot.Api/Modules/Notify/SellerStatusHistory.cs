using Npgsql;

namespace FloraBot.Api.Modules.Notify;

public sealed record SellerStatusHistoryItem(
    string Id,
    Guid? ActorId,
    string Action,
    string? FromStatus,
    string? ToStatus,
    string? Reason,
    DateTime CreatedAt);

public sealed record SellerStatusHistoryResponse(
    List<SellerStatusHistoryItem> Items,
    int Page,
    bool HasMore);

public static class SellerStatusHistory
{
    public static async Task<SellerStatusHistoryResponse> ReadAsync(
        NpgsqlDataSource data,
        Guid sellerId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await using var command = data.CreateCommand("""
            SELECT id::text,
                   actor_id,
                   action,
                   payload->>'from',
                   coalesce(payload->>'to', replace(action, 'SELLER_STATUS_', '')),
                   payload->>'reason',
                   created_at
            FROM notify.audit_logs
            WHERE entity_type = 'sellers'
              AND entity_id = @id
              AND action IN ('SELLER_STATUS_ACTIVE', 'SELLER_STATUS_SUSPENDED')
            ORDER BY id DESC
            LIMIT @limit OFFSET @offset
            """);

        command.Parameters.AddWithValue("id", sellerId);
        command.Parameters.AddWithValue("limit", pageSize + 1);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var items = new List<SellerStatusHistoryItem>();
        while (await reader.ReadAsync(ct))
        {
            items.Add(new SellerStatusHistoryItem(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetDateTime(6)));
        }

        return new SellerStatusHistoryResponse(items.Take(pageSize).ToList(), page, items.Count > pageSize);
    }
}
