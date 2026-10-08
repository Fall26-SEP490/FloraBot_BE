using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Identity;

public static class AdminApprovers
{
    public static async Task<HashSet<Guid>> ReadAsync(FloraDbContext db, Guid[] ids, CancellationToken ct) =>
        (await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id) && x.Role == "ADMIN")
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
}
