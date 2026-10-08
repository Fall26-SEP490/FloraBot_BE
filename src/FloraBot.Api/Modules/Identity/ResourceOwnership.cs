using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public static class ResourceOwnership
{
    public static async Task<Guid?> SellerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM identity.sellers WHERE id=@id FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(ct) is Guid owner ? owner : null;
    }
}
