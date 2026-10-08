using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public static class ResourceOwnership
{
    public static Task<Guid?> SlotSellerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct) =>
        ReadLockedAsync(connection, transaction, "SELECT current_seller_id FROM kiosk_ops.slots WHERE id=@id FOR UPDATE", id, ct);

    public static Task<Guid?> AccessorySellerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct) =>
        ReadLockedAsync(connection, transaction, "SELECT seller_id FROM kiosk_ops.accessories WHERE id=@id FOR UPDATE", id, ct);

    public static Task<Guid?> CommandKioskAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct) =>
        ReadLockedAsync(connection, transaction, "SELECT kiosk_id FROM kiosk_ops.unlock_tokens WHERE cmd_id=@id FOR UPDATE", id, ct);

    private static async Task<Guid?> ReadLockedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(ct) is Guid owner ? owner : null;
    }
}
