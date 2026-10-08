using Npgsql;

namespace FloraBot.Api.Modules.Catalog;

public sealed record StaffStockProduct(Guid Id, string Name);
public static class StaffStockProducts
{
    public static async Task<List<StaffStockProduct>> Read(NpgsqlDataSource data, Guid sellerId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT id,name FROM catalog.flower_products WHERE seller_id=@id AND status='ACTIVE' ORDER BY name,id");
        command.Parameters.AddWithValue("id", sellerId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<StaffStockProduct>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetGuid(0), reader.GetString(1)));
        return rows;
    }
}
