using System.Security.Claims;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record KioskLocationChangeResponse(Guid Id, Guid KioskId, Guid SellerId,
    string Address, double Latitude, double Longitude, string Reason, string Status,
    DateTime CreatedAt, Guid? ReviewedBy, DateTime? ReviewedAt, string? ReviewNote);
public sealed record ReviewKioskLocationChangeRequest(string Decision, string? Note);

public static class KioskLocationChange
{
    public static async Task<IResult> SubmitAsync(Guid sellerId, Guid kioskId,
        UpdateKioskLocationRequest input, NpgsqlDataSource data, HttpContext http, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor) ||
            !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var claimSeller) ||
            claimSeller != sellerId) return Results.Forbid();
        var address = input.Address?.Trim();
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? "Yêu cầu thay đổi vị trí kiosk" : input.Reason.Trim();
        if (string.IsNullOrWhiteSpace(address) || address.Length > 300 || address.Any(char.IsControl) ||
            reason.Length is < 5 or > 1000 || reason.Any(char.IsControl) ||
            input.Latitude is not double lat || !double.IsFinite(lat) || lat is < -90 or > 90 ||
            input.Longitude is not double lon || !double.IsFinite(lon) || lon is < -180 or > 180)
            return Results.Problem(statusCode: 400, detail: "Địa chỉ, tọa độ hoặc lý do không hợp lệ.");

        await using var conn = await data.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var seller = new NpgsqlCommand("""
            SELECT 1 FROM identity.users u JOIN identity.sellers s ON s.id=u.seller_id
            WHERE u.id=@actor AND u.role='SELLER' AND u.status='ACTIVE'
              AND s.id=@seller AND s.status='ACTIVE' FOR SHARE OF u,s
            """, conn, tx))
        {
            seller.Parameters.AddWithValue("actor", actor);
            seller.Parameters.AddWithValue("seller", sellerId);
            if (await seller.ExecuteScalarAsync(ct) is null) return Results.Forbid();
        }
        await using (var kiosk = new NpgsqlCommand("SELECT 1 FROM kiosk_ops.kiosks WHERE id=@id AND seller_id=@seller AND status<>'DISABLED' FOR UPDATE", conn, tx))
        {
            kiosk.Parameters.AddWithValue("id", kioskId);
            kiosk.Parameters.AddWithValue("seller", sellerId);
            if (await kiosk.ExecuteScalarAsync(ct) is null) return Results.NotFound();
        }
        await using (var pending = new NpgsqlCommand("SELECT 1 FROM kiosk_ops.kiosk_location_requests WHERE kiosk_id=@id AND status='REQUESTED'", conn, tx))
        {
            pending.Parameters.AddWithValue("id", kioskId);
            if (await pending.ExecuteScalarAsync(ct) is not null)
                return Results.Problem(statusCode: 409, detail: "Kiosk đã có yêu cầu đổi vị trí đang chờ xử lý.");
        }
        var id = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO kiosk_ops.kiosk_location_requests
              (id,kiosk_id,seller_id,requested_by,address,latitude,longitude,reason)
            VALUES(@id,@kiosk,@seller,@actor,@address,@lat,@lon,@reason)
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("kiosk", kioskId);
            insert.Parameters.AddWithValue("seller", sellerId);
            insert.Parameters.AddWithValue("actor", actor);
            insert.Parameters.AddWithValue("address", address);
            insert.Parameters.AddWithValue("lat", lat);
            insert.Parameters.AddWithValue("lon", lon);
            insert.Parameters.AddWithValue("reason", reason);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var audit = new NpgsqlCommand("""
            SELECT flow.audit(@actor,'KIOSK_LOCATION_CHANGE_REQUESTED','kiosk_location_requests',@id,
              jsonb_build_object('kiosk_id',@kiosk,'seller_id',@seller,'address',@address))
            """, conn, tx))
        {
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("id", id);
            audit.Parameters.AddWithValue("kiosk", kioskId);
            audit.Parameters.AddWithValue("seller", sellerId);
            audit.Parameters.AddWithValue("address", address);
            await audit.ExecuteNonQueryAsync(ct);
        }
        var created = await ReadAsync(conn, tx, id, ct);
        await tx.CommitAsync(ct);
        return Results.Accepted($"/api/sellers/{sellerId}/kiosks/{kioskId}/location-requests/{id}", created);
    }

    public static void MapKioskLocationChange(this WebApplication app)
    {
        app.MapGet("/api/sellers/{sellerId:guid}/kiosks/{kioskId:guid}/location-requests", async (
            Guid sellerId, Guid kioskId, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor) ||
                !Guid.TryParse(http.User.FindFirstValue("seller_id"), out var claim) || claim != sellerId)
                return Results.Forbid();
            await using var conn = await data.OpenConnectionAsync(ct);
            if (!await ActiveActor(conn, null, actor, "SELLER", ct))
                return Results.Forbid();
            var items = new List<KioskLocationChangeResponse>();
            await using var cmd = new NpgsqlCommand("""
                SELECT r.id,r.kiosk_id,r.seller_id,r.address,r.latitude,r.longitude,r.reason,
                       r.status,r.created_at,r.reviewed_by,r.reviewed_at,r.review_note
                FROM kiosk_ops.kiosk_location_requests r
                WHERE r.seller_id=@seller AND r.kiosk_id=@kiosk
                ORDER BY r.created_at DESC LIMIT 50
                """, conn);
            cmd.Parameters.AddWithValue("seller", sellerId);
            cmd.Parameters.AddWithValue("kiosk", kioskId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(Map(reader));
            return Results.Ok(items);
        }).RequireAuthorization("Seller", "SameSeller").WithTags("KioskOps");

        app.MapGet("/api/operations/kiosk-location-requests", async (
            string? status, NpgsqlDataSource data, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            if (status is not null && status is not ("REQUESTED" or "APPROVED" or "REJECTED")) return Results.BadRequest();
            await using var conn = await data.OpenConnectionAsync(ct);
            if (!await ActiveActor(conn, null, actor, "OPERATIONS_MANAGER", ct)) return Results.Forbid();
            var items = new List<KioskLocationChangeResponse>();
            await using var cmd = new NpgsqlCommand("""
                SELECT r.id,r.kiosk_id,r.seller_id,r.address,r.latitude,r.longitude,r.reason,
                       r.status,r.created_at,r.reviewed_by,r.reviewed_at,r.review_note
                FROM kiosk_ops.kiosk_location_requests r
                JOIN kiosk_ops.manager_kiosks m ON m.kiosk_id=r.kiosk_id AND m.manager_id=@actor
                WHERE (@status IS NULL OR r.status=@status)
                ORDER BY r.created_at DESC LIMIT 100
                """, conn);
            cmd.Parameters.AddWithValue("actor", actor);
            cmd.Parameters.Add("status", NpgsqlDbType.Text).Value = (object?)status ?? DBNull.Value;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) items.Add(Map(reader));
            return Results.Ok(items);
        }).RequireAuthorization("OperationsManager").WithTags("KioskOps");

        app.MapPost("/api/operations/kiosk-location-requests/{id:guid}/review", async (
            Guid id, ReviewKioskLocationChangeRequest input, NpgsqlDataSource data,
            HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (!Guid.TryParse(http.User.FindFirstValue("sub"), out var actor)) return Results.Unauthorized();
            var decision = input.Decision?.Trim().ToUpperInvariant();
            var note = input.Note?.Trim();
            if (decision is not ("APPROVED" or "REJECTED") ||
                (decision == "REJECTED" && (note is null || note.Length < 5)) ||
                note?.Length > 1000 || note?.Any(char.IsControl) == true) return Results.BadRequest();
            await using var conn = await data.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            if (!await ActiveActor(conn, tx, actor, "OPERATIONS_MANAGER", ct)) return Results.Forbid();
            Guid kioskId;
            await using (var lookup = new NpgsqlCommand("SELECT kiosk_id FROM kiosk_ops.kiosk_location_requests WHERE id=@id", conn, tx))
            {
                lookup.Parameters.AddWithValue("id", id);
                if (await lookup.ExecuteScalarAsync(ct) is not Guid value) return Results.NotFound();
                kioskId = value;
            }
            string? oldAddress;
            double? oldLat, oldLon;
            Guid? currentSeller;
            await using (var kiosk = new NpgsqlCommand("SELECT seller_id,address,latitude,longitude FROM kiosk_ops.kiosks WHERE id=@id FOR UPDATE", conn, tx))
            {
                kiosk.Parameters.AddWithValue("id", kioskId);
                await using var reader = await kiosk.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                currentSeller = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                oldAddress = reader.IsDBNull(1) ? null : reader.GetString(1);
                oldLat = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                oldLon = reader.IsDBNull(3) ? null : reader.GetDouble(3);
            }
            await using (var scope = new NpgsqlCommand("SELECT 1 FROM kiosk_ops.manager_kiosks WHERE kiosk_id=@kiosk AND manager_id=@actor FOR SHARE", conn, tx))
            {
                scope.Parameters.AddWithValue("kiosk", kioskId);
                scope.Parameters.AddWithValue("actor", actor);
                if (await scope.ExecuteScalarAsync(ct) is null) return Results.Forbid();
            }
            KioskLocationChangeResponse? request;
            await using (var locked = new NpgsqlCommand("""
                SELECT id,kiosk_id,seller_id,address,latitude,longitude,reason,status,
                       created_at,reviewed_by,reviewed_at,review_note
                FROM kiosk_ops.kiosk_location_requests WHERE id=@id FOR UPDATE
                """, conn, tx))
            {
                locked.Parameters.AddWithValue("id", id);
                await using var reader = await locked.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct)) return Results.NotFound();
                request = Map(reader);
            }
            if (request.Status != "REQUESTED") return Results.Problem(statusCode: 409, detail: "Yêu cầu đã được xử lý.");
            if (request.SellerId != currentSeller) return Results.Problem(statusCode: 409, detail: "Kiosk đã đổi chủ thuê.");
            if (decision == "APPROVED")
            {
                await using var updateKiosk = new NpgsqlCommand("""
                    UPDATE kiosk_ops.kiosks SET address=@address,latitude=@lat,longitude=@lon,
                      updated_at=public.app_now() WHERE id=@id
                    """, conn, tx);
                updateKiosk.Parameters.AddWithValue("id", kioskId);
                updateKiosk.Parameters.AddWithValue("address", request.Address);
                updateKiosk.Parameters.AddWithValue("lat", request.Latitude);
                updateKiosk.Parameters.AddWithValue("lon", request.Longitude);
                await updateKiosk.ExecuteNonQueryAsync(ct);
            }
            await using (var updateRequest = new NpgsqlCommand("""
                UPDATE kiosk_ops.kiosk_location_requests
                SET status=@status,reviewed_by=@actor,review_note=@note,
                    old_address=@oldAddress,old_latitude=@oldLat,old_longitude=@oldLon,
                    reviewed_at=public.app_now() WHERE id=@id
                """, conn, tx))
            {
                updateRequest.Parameters.AddWithValue("id", id);
                updateRequest.Parameters.AddWithValue("status", decision);
                updateRequest.Parameters.AddWithValue("actor", actor);
                updateRequest.Parameters.Add("note", NpgsqlDbType.Text).Value = (object?)note ?? DBNull.Value;
                updateRequest.Parameters.Add("oldAddress", NpgsqlDbType.Text).Value = (object?)oldAddress ?? DBNull.Value;
                updateRequest.Parameters.Add("oldLat", NpgsqlDbType.Double).Value = (object?)oldLat ?? DBNull.Value;
                updateRequest.Parameters.Add("oldLon", NpgsqlDbType.Double).Value = (object?)oldLon ?? DBNull.Value;
                await updateRequest.ExecuteNonQueryAsync(ct);
            }
            await using (var audit = new NpgsqlCommand("""
                SELECT flow.audit(@actor,'KIOSK_LOCATION_CHANGE_REVIEWED','kiosk_location_requests',@id,
                  jsonb_build_object('kiosk_id',@kiosk,'decision',@decision,'old_address',@old,'new_address',@new))
                """, conn, tx))
            {
                audit.Parameters.AddWithValue("actor", actor);
                audit.Parameters.AddWithValue("id", id);
                audit.Parameters.AddWithValue("kiosk", kioskId);
                audit.Parameters.AddWithValue("decision", decision);
                audit.Parameters.Add("old", NpgsqlDbType.Text).Value = (object?)oldAddress ?? DBNull.Value;
                audit.Parameters.AddWithValue("new", request.Address);
                await audit.ExecuteNonQueryAsync(ct);
            }
            var result = await ReadAsync(conn, tx, id, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization("OperationsManager").WithTags("KioskOps")
          .Produces<KioskLocationChangeResponse>();
    }

    private static async Task<bool> ActiveActor(NpgsqlConnection conn, NpgsqlTransaction? tx,
        Guid actor, string role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM identity.users WHERE id=@id AND role=@role AND status='ACTIVE' FOR SHARE", conn, tx);
        cmd.Parameters.AddWithValue("id", actor);
        cmd.Parameters.AddWithValue("role", role);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<KioskLocationChangeResponse?> ReadAsync(NpgsqlConnection conn,
        NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT id,kiosk_id,seller_id,address,latitude,longitude,reason,status,
                   created_at,reviewed_by,reviewed_at,review_note
            FROM kiosk_ops.kiosk_location_requests WHERE id=@id
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private static KioskLocationChangeResponse Map(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
        reader.GetDouble(4), reader.GetDouble(5), reader.GetString(6), reader.GetString(7),
        reader.GetDateTime(8), reader.IsDBNull(9) ? null : reader.GetGuid(9),
        reader.IsDBNull(10) ? null : reader.GetDateTime(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));
}
