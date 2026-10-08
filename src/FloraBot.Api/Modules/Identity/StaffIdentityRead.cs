using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record StaffDirectoryItem(Guid Id, string Name);

public static class StaffIdentityRead
{
    public static Task<List<StaffDirectoryItem>> ActiveStaff(FloraDbContext db, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(x => x.Role == "STAFF" && x.Status == "ACTIVE")
            .OrderBy(x => x.FullName).ThenBy(x => x.Id).Select(x => new StaffDirectoryItem(x.Id, x.FullName)).ToListAsync(ct);

    public static Task<Dictionary<Guid, string>> ActiveSellers(NpgsqlDataSource data, CancellationToken ct) =>
        Names(data, "SELECT id,shop_name FROM identity.sellers WHERE status='ACTIVE'", null, ct);
    public static Task<Dictionary<Guid, string>> Users(NpgsqlDataSource data, Guid[] ids, CancellationToken ct) =>
        Names(data, "SELECT id,full_name FROM identity.users WHERE id=ANY(@ids)", ids, ct);
    public static Task<Dictionary<Guid, string>> Sellers(NpgsqlDataSource data, Guid[] ids, CancellationToken ct) =>
        Names(data, "SELECT id,shop_name FROM identity.sellers WHERE id=ANY(@ids)", ids, ct);
    private static async Task<Dictionary<Guid, string>> Names(NpgsqlDataSource data, string sql, Guid[]? ids, CancellationToken ct)
    {
        await using var command = data.CreateCommand(sql);
        if (ids is not null) command.Parameters.AddWithValue("ids", ids);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<Guid, string>();
        while (await reader.ReadAsync(ct)) result[reader.GetGuid(0)] = reader.GetString(1);
        return result;
    }
}
