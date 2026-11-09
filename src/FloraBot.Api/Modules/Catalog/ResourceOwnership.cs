using Npgsql;

namespace FloraBot.Api.Modules.Catalog;

public static class ResourceOwnership
{
    public static async Task<Guid?> ProductSellerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct,
        bool forStocking = false)
    {
        // Stocking locks the slot before deducting quantity. KEY SHARE keeps
        // this lookup compatible with that quantity-only row update, including
        // sensor maintenance which already owns its slot lock. The stock
        // trigger rechecks product status and ownership during deduction.
        var sql = forStocking
            ? "SELECT seller_id FROM catalog.flower_products WHERE id=@id FOR KEY SHARE"
            : "SELECT seller_id FROM catalog.flower_products WHERE id=@id FOR UPDATE";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(ct) is Guid owner ? owner : null;
    }
}
