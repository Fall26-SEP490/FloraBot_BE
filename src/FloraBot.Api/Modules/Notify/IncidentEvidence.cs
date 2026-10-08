using FloraBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FloraBot.Api.Modules.Notify;

public sealed record IncidentPhoto(Guid Id, string Url, DateTime CreatedAt);
public sealed record IncidentEvidenceResponse(List<IncidentPhoto> Items, bool HasMore);

public static class IncidentEvidence
{
    public static async Task<IncidentEvidenceResponse> ReadAsync(FloraDbContext db, Guid incidentId, CancellationToken ct)
    {
        var rows = await db.Attachments.AsNoTracking()
            .Where(x => x.OwnerService == "ordering" && x.OwnerType == "dispute" && x.OwnerId == incidentId && x.Phase == "EVIDENCE")
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => new IncidentPhoto(x.Id, x.FileUrl, x.CreatedAt)).Take(21).ToListAsync(ct);
        return new(rows.Take(20).Select(row => row.Url.Contains("/image/authenticated/", StringComparison.Ordinal)
            ? row with { Url = $"/api/admin/evidence/{row.Id}" } : row).ToList(), rows.Count > 20);
    }
}
