using System.Security.Cryptography;
using System.Text;
using DotNetCore.CAP;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Notify;
using Npgsql;
using StackExchange.Redis;

namespace FloraBot.Api.Modules.Ordering;

public sealed record PreorderPickupOtpSent(Guid RequestId, Guid OrderId, string RecipientMasked,
    DateTimeOffset ExpiresAt, int ResendAfterSeconds);
public sealed record PreorderPickupOtpOpenRequest(string Code, Guid AttemptId);
public sealed record PreorderPickupOtpOpened(Guid AttemptId, Guid OrderId, string OrderCode,
    string ProductName, string SlotCode, string OrderStatus, Guid CommandId,
    string WatchToken, DateTimeOffset WatchExpiresAt);
public sealed record PreorderPickupOtpStatus(Guid OrderId, string OrderCode, string ProductName,
    string SlotCode, string OrderStatus, string CommandStatus);
public sealed class PickupOtpFailure(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed class PreorderPickupOtp(NpgsqlDataSource source, ITransactionalEmailSender sender,
    EmailSettings emailSettings, IConfiguration config, IConnectionMultiplexer redis,
    ILogger<PreorderPickupOtp> logger, ICapPublisher publisher)
{
    private sealed record Eligible(Guid RequestId, Guid OrderId, Guid SellerId, Guid KioskId,
        string Email, string OrderCode, string ProductName, string SlotCode, string KioskName,
        string Address, DateTimeOffset PickupAt, DateTimeOffset PickupBefore, DateTimeOffset Now,
        string OrderStatus, string KioskStatus);

    private string Digest(string purpose, string value) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(config["JWT_SIGNING_KEY"]!), Encoding.UTF8.GetBytes($"{purpose}|{value}")));
    private string CodeHash(Guid kiosk, string code) => Digest("preorder-pickup-code-v1", $"{kiosk}|{code}");
    private string WatchToken(Guid kiosk, Guid attempt) => Digest("preorder-pickup-watch-v1", $"{kiosk}|{attempt}");
    private string EmailHash(string email) => Digest("preorder-pickup-email-v1", email.Trim().ToLowerInvariant());
    private static string Mask(string email)
    {
        var at = email.LastIndexOf('@');
        return at > 0 ? $"{email[0]}***{email[at..]}" : "***";
    }
    private static NpgsqlCommand Cmd(NpgsqlConnection c, NpgsqlTransaction? t, string sql,
        params (string Key, object Value)[] args)
    {
        var command = new NpgsqlCommand(sql, c, t) { CommandTimeout = 10 };
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        return command;
    }
    private static async Task Lock(NpgsqlConnection c, NpgsqlTransaction t, string key, CancellationToken ct)
    {
        await using var cmd = Cmd(c, t, "SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", ("key", key));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task<Eligible> ReadEligible(NpgsqlConnection c, NpgsqlTransaction t,
        Guid request, Guid kiosk, bool opening, CancellationToken ct)
    {
        await using var cmd = Cmd(c, t, """
            SELECT w.id,o.id,o.seller_id,o.kiosk_id,u.email,o.order_code,p.name,s.slot_code,
              k.name,k.address,w.pickup_at,w.pickup_before,public.app_now(),o.status,k.status
            FROM ordering.web_requests w JOIN ordering.orders o ON o.id=w.order_id
            JOIN identity.users u ON u.id=o.customer_id AND u.status='ACTIVE'
            JOIN kiosk_ops.kiosks k ON k.id=o.kiosk_id AND k.seller_id=o.seller_id
            JOIN kiosk_ops.slots s ON s.hold_order_id=o.id AND s.kiosk_id=k.id
              AND s.current_seller_id=o.seller_id AND s.status='HELD' AND s.zone='PREORDER'
              AND s.hold_until>=public.app_now()
            JOIN kiosk_ops.bouquets b ON b.id=s.bouquet_id AND b.status='SOLD' AND b.seller_id=o.seller_id
            JOIN catalog.flower_products p ON p.id=b.product_id AND p.id=w.product_id AND p.seller_id=o.seller_id
            JOIN ordering.order_items item ON item.order_id=o.id AND item.slot_id=s.id
              AND item.bouquet_id=b.id AND item.line_status='ACTIVE' AND item.item_type='BOUQUET'
            WHERE w.id=@request AND w.kiosk_id=@kiosk AND w.kind='CUSTOM' AND w.state='ORDERED'
              AND w.seller_id=o.seller_id AND w.customer_id=o.customer_id
              AND o.status IN ('PAID','DISPENSING') AND w.pickup_before>public.app_now()
              AND k.status NOT IN ('DISABLED','MAINTENANCE')
              AND (NOT @opening OR k.last_heartbeat_at>public.app_now()-make_interval(secs=>flow.cfg('heartbeat_offline_seconds')))
              AND (SELECT count(*) FROM kiosk_ops.slots held WHERE held.hold_order_id=o.id AND held.status='HELD')=1
              AND EXISTS(SELECT 1 FROM payment.payments pay WHERE pay.checkout_id=o.checkout_id
                AND pay.kind='CHARGE' AND pay.purpose='ORDER_CHECKOUT' AND pay.status='SUCCEEDED')
            FOR UPDATE OF w,o,s,b FOR SHARE OF u,k
            """, ("request", request), ("kiosk", kiosk), ("opening", opening));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new PickupOtpFailure(409, "Đơn chưa đủ điều kiện nhận: cần thanh toán thành công và hoa đã ở ô PREORDER của kiosk này.");
        var row = new Eligible(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
            reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetString(9), reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12), reader.GetString(13), reader.GetString(14));
        if (!EmailSettings.IsAddress(row.Email)) throw new PickupOtpFailure(409, "Tài khoản đặt đơn chưa có email đăng nhập hợp lệ.");
        if (opening && (row.KioskStatus != "ONLINE" || row.Now < row.PickupAt))
            throw new PickupOtpFailure(409, "Kiosk phải trực tuyến và đã đến khung giờ nhận hoa.");
        return row;
    }
    private static async Task EnsureDoorsIdle(NpgsqlConnection c, NpgsqlTransaction t, Guid order, CancellationToken ct)
    {
        await using var cmd = Cmd(c, t, """
            SELECT EXISTS(SELECT 1 FROM kiosk_ops.unlock_tokens WHERE order_id=@order
              AND (status='OPENED' OR (door_opened_at IS NOT NULL AND door_closed_at IS NULL)
                OR (status IN ('ISSUED','SENT','ACKED') AND expires_at>public.app_now())))
            """, ("order", order));
        if ((bool)(await cmd.ExecuteScalarAsync(ct))!) throw new PickupOtpFailure(409, "Đang có lệnh mở ô hoặc cửa chưa đóng. Kiểm tra tủ trước khi gửi mã hoặc thử lại.");
    }

    public async Task<PreorderPickupOtpSent> Send(Guid request, Guid actor, CancellationToken ct)
    {
        if (emailSettings.Provider == EmailProvider.Disabled) throw new PickupOtpFailure(503, "Dịch vụ email chưa được cấu hình.");
        Eligible row; Guid generation = Guid.CreateVersion7(); string code;
        await using (var c = await source.OpenConnectionAsync(ct))
        await using (var t = await c.BeginTransactionAsync(ct))
        {
            Guid order, kiosk;
            await using (var scope = Cmd(c, t, """
                SELECT w.order_id,w.kiosk_id FROM ordering.web_requests w
                JOIN identity.users u ON u.id=@actor AND u.role='SELLER' AND u.status='ACTIVE' AND u.seller_id=w.seller_id
                WHERE w.id=@request AND w.order_id IS NOT NULL FOR SHARE OF u
                """, ("actor", actor), ("request", request)))
            await using (var reader = await scope.ExecuteReaderAsync(ct))
            {
                if (!await reader.ReadAsync(ct)) throw new PickupOtpFailure(404, "Không tìm thấy đơn của shop để gửi mã nhận hoa.");
                order = reader.GetGuid(0); kiosk = reader.GetGuid(1);
            }
            await Lock(c, t, $"preorder-pickup-code:{kiosk}", ct);
            await Lock(c, t, $"preorder-pickup:{order}", ct);
            row = await ReadEligible(c, t, request, kiosk, false, ct);
            await EnsureDoorsIdle(c, t, order, ct);
            int count = 1; var window = row.Now;
            await using (var old = Cmd(c, t, "SELECT issued_at,send_window_at,send_count FROM ordering.preorder_pickup_otps WHERE request_id=@request FOR UPDATE", ("request", request)))
            await using (var reader = await old.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    var issued = reader.GetFieldValue<DateTimeOffset>(0);
                    if (row.Now < issued.AddSeconds(60)) throw new PickupOtpFailure(429, "Chờ ít nhất 60 giây trước khi gửi lại mã.");
                    window = reader.GetFieldValue<DateTimeOffset>(1);
                    if (row.Now < window.AddHours(1)) count = reader.GetInt32(2) + 1; else window = row.Now;
                    if (count > 5) throw new PickupOtpFailure(429, "Đã gửi mã tối đa 5 lần trong một giờ. Vui lòng liên hệ hỗ trợ nếu chưa nhận được email.");
                }
            }
            await using (var expire = Cmd(c, t, "UPDATE ordering.preorder_pickup_otps SET status='REVOKED' WHERE kiosk_id=@kiosk AND status IN ('SENDING','ACTIVE') AND expires_at<=public.app_now()", ("kiosk", kiosk)))
                await expire.ExecuteNonQueryAsync(ct);
            // Serialize senders per kiosk, then reserve a unique live numeric code.
            for (var attempt = 0; ; attempt++)
            {
                if (attempt == 10) throw new PickupOtpFailure(503, "Chưa tạo được mã nhận hoa. Thử lại sau.");
                code = RandomNumberGenerator.GetInt32(0, 100000000).ToString("D8");
                await using var collision = Cmd(c, t, "SELECT EXISTS(SELECT 1 FROM ordering.preorder_pickup_otps WHERE kiosk_id=@kiosk AND code_hash=@hash AND status IN ('SENDING','ACTIVE'))", ("kiosk", kiosk), ("hash", CodeHash(kiosk, code)));
                if (!(bool)(await collision.ExecuteScalarAsync(ct))!) break;
            }
            await using (var save = Cmd(c, t, """
                INSERT INTO ordering.preorder_pickup_otps(request_id,order_id,kiosk_id,generation_id,code_hash,email_hash,status,
                  issued_at,expires_at,sent_by,send_window_at,send_count)
                VALUES(@request,@order,@kiosk,@generation,@code,@email,'SENDING',@now,@expires,@actor,@window,@count)
                ON CONFLICT(request_id) DO UPDATE SET generation_id=excluded.generation_id,code_hash=excluded.code_hash,
                  email_hash=excluded.email_hash,status='SENDING',issued_at=excluded.issued_at,expires_at=excluded.expires_at,
                  sent_by=excluded.sent_by,send_window_at=excluded.send_window_at,send_count=excluded.send_count,used_at=NULL;
                SELECT flow.audit(@actor,'PREORDER_PICKUP_OTP_REQUESTED','orders',@order,jsonb_build_object('requestId',@request,'generationId',@generation));
                """, ("request", request), ("order", order), ("kiosk", kiosk), ("generation", generation), ("code", CodeHash(kiosk, code)),
                ("email", EmailHash(row.Email)), ("now", row.Now), ("expires", row.PickupBefore), ("actor", actor), ("window", window), ("count", count)))
                await save.ExecuteNonQueryAsync(ct);
            await t.CommitAsync(ct);
        }
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh");
            var from = TimeZoneInfo.ConvertTime(row.PickupAt, zone).ToString("dd/MM/yyyy HH:mm");
            var until = TimeZoneInfo.ConvertTime(row.PickupBefore, zone).ToString("dd/MM/yyyy HH:mm");
            var text = $"Mã nhận hoa FloraBot: {code}\nĐơn: {row.OrderCode}\nHoa: {row.ProductName}\nKiosk: {row.KioskName}\nĐịa chỉ: {row.Address}\nNhận từ {from} đến {until} (giờ Việt Nam).\nNhập 8 chữ số trên màn hình Nhận đơn đặt trước tại đúng kiosk. Mã chỉ dùng một lần và mã cũ hết hiệu lực khi shop gửi lại. Không chia sẻ mã.";
            await sender.SendAsync(new TransactionalEmail(row.Email, "FloraBot - Mã OTP nhận hoa đặt trước", text), ct);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await FinishDelivery(request, generation, false);
            logger.LogWarning("Pickup OTP email not confirmed for request {RequestId}, generation {GenerationId}", request, generation);
            throw new PickupOtpFailure(503, "Chưa xác nhận gửi email thành công. Kiểm tra hộp thư và trạng thái trước khi yêu cầu gửi lại sau 60 giây.");
        }
        if (!await FinishDelivery(request, generation, true)) throw new PickupOtpFailure(503, "Mã vừa gửi chưa được kích hoạt hoặc đã hết hạn. Liên hệ shop để gửi mã mới.");
        return new(request, row.OrderId, Mask(row.Email), row.PickupBefore, 60);
    }

    private async Task<bool> FinishDelivery(Guid request, Guid generation, bool accepted)
    {
        await using var c = await source.OpenConnectionAsync(CancellationToken.None);
        await using var cmd = Cmd(c, null, """
            UPDATE ordering.preorder_pickup_otps SET status=@status
            WHERE request_id=@request AND generation_id=@generation AND status='SENDING'
              AND (NOT @accepted OR expires_at>public.app_now())
            """, ("request", request), ("generation", generation), ("accepted", accepted), ("status", accepted ? "ACTIVE" : "FAILED"));
        return await cmd.ExecuteNonQueryAsync(CancellationToken.None) == 1;
    }

    public async Task<PreorderPickupOtpOpened> Open(Guid kiosk, string code, Guid attempt, CancellationToken ct)
    {
        var hash = CodeHash(kiosk, code);
        await using var c = await source.OpenConnectionAsync(ct);
        using var outbox = await c.BeginTransactionAsync(publisher, cancellationToken: ct);
        var t = (NpgsqlTransaction)outbox.DbTransaction!;
        await Lock(c, t, $"preorder-pickup-attempt:{attempt}", ct);
        // A lost response retried with the same attempt returns the same command, never another unlock.
        var previous = await ReadAttempt(c, t, kiosk, attempt, hash, ct);
        if (previous is not null) { await outbox.CommitAsync(ct); return previous; }
        var db = redis.GetDatabase();
        var failureKey = $"preorder-pickup:wrong:{kiosk}";
        if (int.TryParse((await db.StringGetAsync(failureKey)).ToString(), out var failures) && failures >= 5)
            throw new PickupOtpFailure(429, "Đã nhập sai nhiều lần. Vui lòng chờ 5 phút hoặc liên hệ hỗ trợ.");
        Guid request, order, generation;
        await using (var find = Cmd(c, t, "SELECT request_id,order_id,generation_id FROM ordering.preorder_pickup_otps WHERE kiosk_id=@kiosk AND code_hash=@hash AND status='ACTIVE' AND expires_at>public.app_now()", ("kiosk", kiosk), ("hash", hash)))
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                await db.ScriptEvaluateAsync("local n=redis.call('INCR',KEYS[1]); if n==1 then redis.call('EXPIRE',KEYS[1],300) end; return n", [failureKey]);
                throw new PickupOtpFailure(401, "Mã không đúng, đã dùng hoặc đã hết hạn. Kiểm tra email và đúng kiosk nhận hoa.");
            }
            request = reader.GetGuid(0); order = reader.GetGuid(1); generation = reader.GetGuid(2);
        }
        await Lock(c, t, $"preorder-pickup:{order}", ct);
        previous = await ReadAttempt(c, t, kiosk, attempt, hash, ct);
        if (previous is not null) { await outbox.CommitAsync(ct); return previous; }
        var row = await ReadEligible(c, t, request, kiosk, true, ct);
        await EnsureDoorsIdle(c, t, order, ct);
        var watermark = await TransactionChanges.WatermarkAsync(c, t, ct);
        await using (var consume = Cmd(c, t, """
            UPDATE ordering.preorder_pickup_otps SET status='USED',used_at=public.app_now()
            WHERE request_id=@request AND generation_id=@generation AND code_hash=@hash AND email_hash=@email
              AND status='ACTIVE' AND expires_at>public.app_now()
            """, ("request", request), ("generation", generation), ("hash", hash), ("email", EmailHash(row.Email))))
            if (await consume.ExecuteNonQueryAsync(ct) != 1) throw new PickupOtpFailure(401, "Mã không còn hiệu lực. Liên hệ shop để gửi lại mã.");
        Guid command;
        await using (var issue = Cmd(c, t, "SELECT flow.issue_pickup_tokens(@order)", ("order", order)))
        {
            var value = await issue.ExecuteScalarAsync(ct);
            if (value is not Guid id) throw new PickupOtpFailure(409, "Chưa tạo được lệnh mở đúng ô của đơn.");
            command = id;
        }
        await using (var capExpiry = Cmd(c, t, """
            UPDATE kiosk_ops.unlock_tokens SET expires_at=least(expires_at,@deadline)
            WHERE cmd_id=@command AND @deadline>public.app_now()
            """, ("command", command), ("deadline", row.PickupBefore)))
            if (await capExpiry.ExecuteNonQueryAsync(ct) != 1) throw new PickupOtpFailure(409, "Đã hết khung giờ nhận hoa.");
        var expires = row.Now.AddMinutes(10);
        if (expires > row.PickupBefore) expires = row.PickupBefore;
        await using (var save = Cmd(c, t, """
            INSERT INTO ordering.preorder_pickup_attempts(id,generation_id,order_id,kiosk_id,code_hash,command_id,expires_at)
            VALUES(@attempt,@generation,@order,@kiosk,@hash,@command,@expires);
            SELECT flow.set_order_status(@order,'DISPENSING','KIOSK',NULL,'Nhận preorder bằng OTP email')
              WHERE (SELECT status FROM ordering.orders WHERE id=@order)='PAID';
            SELECT flow.audit(@kiosk,'PREORDER_PICKUP_OTP_USED','orders',@order,
              jsonb_build_object('attemptId',@attempt,'commandId',@command),'KIOSK');
            """, ("attempt", attempt), ("generation", generation), ("order", order), ("kiosk", kiosk),
            ("hash", hash), ("command", command), ("expires", expires))) await save.ExecuteNonQueryAsync(ct);
        await DomainChanges.PublishAsync(c, t, publisher, watermark, ct);
        await outbox.CommitAsync(ct);
        return new(attempt, order, row.OrderCode, row.ProductName, row.SlotCode, "DISPENSING", command, WatchToken(kiosk, attempt), expires);
    }

    private async Task<PreorderPickupOtpOpened?> ReadAttempt(NpgsqlConnection c, NpgsqlTransaction t,
        Guid kiosk, Guid attempt, string hash, CancellationToken ct)
    {
        await using var cmd = Cmd(c, t, """
            SELECT a.order_id,o.order_code,p.name,s.slot_code,o.status,a.command_id,a.expires_at
            FROM ordering.preorder_pickup_attempts a JOIN ordering.orders o ON o.id=a.order_id
            JOIN ordering.web_requests w ON w.order_id=o.id JOIN catalog.flower_products p ON p.id=w.product_id
            JOIN kiosk_ops.unlock_tokens token ON token.cmd_id=a.command_id JOIN kiosk_ops.slots s ON s.id=token.slot_id
            WHERE a.id=@attempt AND a.kiosk_id=@kiosk AND o.kiosk_id=@kiosk AND a.code_hash=@hash AND a.expires_at>public.app_now()
            """, ("attempt", attempt), ("kiosk", kiosk), ("hash", hash));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(attempt, reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetGuid(5), WatchToken(kiosk, attempt), reader.GetFieldValue<DateTimeOffset>(6));
    }
    public async Task<PreorderPickupOtpStatus> Status(Guid kiosk, Guid attempt, string watch, CancellationToken ct)
    {
        var expected = WatchToken(kiosk, attempt);
        if (watch.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(watch), Encoding.ASCII.GetBytes(expected)))
            throw new PickupOtpFailure(404, "Không tìm thấy lượt nhận hoa.");
        await using var c = await source.OpenConnectionAsync(ct);
        await using var cmd = Cmd(c, null, """
            SELECT o.id,o.order_code,p.name,s.slot_code,o.status,token.status
            FROM ordering.preorder_pickup_attempts a JOIN ordering.orders o ON o.id=a.order_id
            JOIN ordering.web_requests w ON w.order_id=o.id JOIN catalog.flower_products p ON p.id=w.product_id
            JOIN kiosk_ops.unlock_tokens token ON token.cmd_id=a.command_id JOIN kiosk_ops.slots s ON s.id=token.slot_id
            WHERE a.id=@attempt AND a.kiosk_id=@kiosk AND o.kiosk_id=@kiosk AND a.expires_at>public.app_now()
            """, ("attempt", attempt), ("kiosk", kiosk));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new PickupOtpFailure(404, "Lượt theo dõi nhận hoa đã hết hạn.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
    }
}
