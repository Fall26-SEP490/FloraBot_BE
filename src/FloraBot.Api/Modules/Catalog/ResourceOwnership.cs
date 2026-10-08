using Npgsql;

namespace FloraBot.Api.Modules.Catalog;

public static class ResourceOwnership
{
    public static async Task<Guid?> ProductSellerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT seller_id FROM catalog.flower_products WHERE id=@id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(ct) is Guid owner ? owner : null;
    }
}
