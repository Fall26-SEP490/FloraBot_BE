using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record PortalMember(Guid Id, string Role, Guid? SellerId);

public sealed class PortalAudience(NpgsqlDataSource data)
{
    public async Task<PortalMember[]> ActiveAsync(Guid[] users, CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT id,role,seller_id FROM identity.users WHERE id=ANY(@users) AND status='ACTIVE' AND role IN ('ADMIN','SELLER')");
        command.Parameters.AddWithValue("users", users);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var members = new List<PortalMember>();
        while (await reader.ReadAsync(ct)) members.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2)));
        return members.ToArray();
    }
}
