using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNetCore.CAP;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.KioskOps;

public sealed record CreateKioskApplicationRequest(
    Guid? PackageId,
    string ShopName,
    string Phone,
    string ContactEmail,
    string InstallationAddress,
    double? Latitude,
    double? Longitude,
    string BankName,
    string BankHolder,
    string BankAccountNumber,
    int? WalkInSlots = null,
    int? PreorderSlots = null,
    int? PricingVersion = null
);

public sealed record CreateAdditionalKioskApplicationRequest(
    Guid? PackageId,
    string InstallationAddress,
    double? Latitude,
    double? Longitude,
    int? WalkInSlots = null,
    int? PreorderSlots = null,
    int? PricingVersion = null
);

public sealed record CancelKioskApplicationRequest(string? Reason);
public sealed record ConfirmKioskApplicationRequest(string Note);
public sealed record ScheduleKioskApplicationRequest(Guid TechnicianId, DateOnly ScheduledDate, string TimeWindow, Guid KioskId);
public sealed record ReworkKioskApplicationRequest(string Reason);
public sealed record RejectKioskApplicationRequest(string Reason);
public sealed record PlatformFailureRequest(string Reason);
public sealed record EnrollKioskRequest(string Code, string Name, string HardwareId, string? MqttClientId, string? Region,
    int? SlotCount = null, Guid? ApplicationId = null);
public sealed record SubmitInstallationReportRequest(string Report);
public sealed record AttachKioskEvidenceRequest(Guid Id, string Reference);
public sealed record ConfirmKioskRefundRequest(string Reference, string EvidenceReference);
public sealed record ProvisionKioskKeyResponse(Guid KioskId, string ApiKey);
public sealed record EnrollKioskResponse(Guid Id, string Code, string Name, int SlotCount, string Status, string ApiKey,
    int WalkInSlots = 0, int PreorderSlots = 0, Guid? ApplicationId = null, Esp32ProvisioningStatus? Provisioning = null);
public sealed record KioskRefundBankResponse(string BankName, string Holder, string AccountNumber);

public sealed record KioskApplicationResponse(
    Guid Id,
    Guid CustomerId,
    Guid PackageId,
    string PackageName,
    long AnnualFee,
    int TotalSlots,
    string ShopName,
    string Phone,
    string ContactEmail,
    string InstallationAddress,
    double Latitude,
    double Longitude,
    string BankName,
    string BankHolder,
    string BankAccountSuffix,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? PaymentId,
    string? PaymentStatus,
    string? ReviewNote,
    DateTime? ConfirmedAt,
    Guid? AssignedTechnicianId,
    string? AssignedTechnicianName,
    Guid? AssignedKioskId,
    string? AssignedKioskCode,
    DateOnly? ScheduledDate,
    string? TimeWindow,
    string? TechnicianReport,
    string? ReworkReason,
    DateTime? HandoverAt,
    Guid? HandoverBy,
    Guid? SellerId,
    bool CanVoluntaryCancel,
    int? WalkInSlots,
    int? PreorderSlots,
    string PricingMode,
    long? WalkInAnnualPrice,
    long? PreorderAnnualPrice,
    int? PricingVersion
);

public sealed record KioskApplicationPage(List<KioskApplicationResponse> Items, int Page, bool HasMore);
public sealed record KioskApplicationDetailResponse(KioskApplicationResponse Application, List<IncidentPhoto> EvidencePhotos);

public sealed record TechnicianInstallationSummary(
    Guid Id,
    string ShopName,
    string Phone,
    string ContactEmail,
    string InstallationAddress,
    double Latitude,
    double Longitude,
    string? KioskCode,
    DateOnly? ScheduledDate,
    string? TimeWindow,
    string Status,
    string? ReworkReason
);

public sealed record TechnicianInstallationDetail(
    Guid Id,
    string ShopName,
    string Phone,
    string ContactEmail,
    string InstallationAddress,
    double Latitude,
    double Longitude,
    Guid? KioskId,
    string? KioskCode,
    DateOnly? ScheduledDate,
    string? TimeWindow,
    string Status,
    string? TechnicianReport,
    string? ReworkReason,
    List<StaffEvidenceItem> Evidence
);

public sealed record EligibleKioskResponse(Guid Id, string Code, string Name, int SlotCount, string Status,
    int WalkInSlots = 0, int PreorderSlots = 0, int UnclassifiedSlots = 0);
public sealed record AdminKioskRefundSummary(
    Guid Id,
    string ShopName,
    long Amount,
    string BankName,
    string BankHolder,
    string BankAccountSuffix,
    string? RefundReason,
    string Status,
    Guid? RefundApprovedBy,
    DateTime CreatedAt
);

public static partial class KioskApplications
{
    [GeneratedRegex("^(0[35789][0-9]{8}|\\+84[35789][0-9]{8})$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^(?<start>([01][0-9]|2[0-3]):[0-5][0-9])\s*-\s*(?<end>([01][0-9]|2[0-3]):[0-5][0-9])$")]
    private static partial Regex TimeWindowRegex();

    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue("sub")!);
    private static string TechEvidenceBinding(Guid actor, Guid appId) => $"tech-kiosk:{actor}:{appId}";

    private static async Task<bool> OwnsApplicationAsync(
        FloraDbContext db,
        HttpContext http,
        KioskApplication application,
        CancellationToken ct)
    {
        var actor = Actor(http);
        if (application.CustomerId != actor) return false;
        var identity = await db.Users.AsNoTracking()
            .Where(x => x.Id == actor)
            .Select(x => new { x.Role, x.Status, x.SellerId })
            .SingleOrDefaultAsync(ct);
        if (identity is null || identity.Status != "ACTIVE") return false;

        var tokenRole = http.User.FindFirstValue("role");
        if (tokenRole == "CUSTOMER")
            return identity.Role == "CUSTOMER" && application.SellerId is null;

        return tokenRole == "SELLER"
            && identity.Role == "SELLER"
            && Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId)
            && identity.SellerId == sellerId
            && application.SellerId == sellerId;
    }

    private static string MaskAccount(string enc, IDataProtectionProvider protection)
    {
        try
        {
            var clear = protection.CreateProtector("bank-account-v1").Unprotect(enc);
            if (clear.Length > 4) return clear[^4..];
            if (clear.Length >= 2) return clear[^2..];
            return "****";
        }
        catch (CryptographicException)
        {
            return "****";
        }
    }

    public static void MapKioskApplications(this WebApplication app)
    {
        // 1. Customer: Apply for annual kiosk lease
        app.MapPost("/api/member/kiosk-applications", async (
            CreateKioskApplicationRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            FloraDbContext db,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var role = http.User.FindFirstValue("role");
            if (role != "CUSTOMER")
                return Results.Problem(statusCode: 403, detail: "Chỉ tài khoản khách hàng (CUSTOMER) mới được đăng ký thuê kiosk.");

            var errors = new Dictionary<string, string[]>();
            if (!ValidSlotDemand(input.WalkInSlots, input.PreorderSlots))
                errors["slots"] = ["Số ô Walk-in và Pre-order phải không âm, tổng từ 1 đến 50."];
            if (input.PricingVersion is null or <= 0)
                errors["pricingVersion"] = ["Cần gửi phiên bản bảng giá đã xem trước khi đăng ký."];
            if (string.IsNullOrWhiteSpace(input.ShopName) || input.ShopName.Trim().Length is < 2 or > 120 || input.ShopName.Any(char.IsControl))
                errors["shopName"] = ["Tên shop từ 2 đến 120 ký tự, không chứa ký tự điều khiển."];
            if (string.IsNullOrWhiteSpace(input.Phone) || !PhoneRegex().IsMatch(input.Phone.Trim()))
                errors["phone"] = ["Số điện thoại Việt Nam hợp lệ."];
            if (string.IsNullOrWhiteSpace(input.ContactEmail) || !EmailSettings.IsAddress(input.ContactEmail.Trim()))
                errors["contactEmail"] = ["Email liên hệ không hợp lệ."];
            if (string.IsNullOrWhiteSpace(input.InstallationAddress) || input.InstallationAddress.Trim().Length is < 1 or > 200 || input.InstallationAddress.Any(char.IsControl))
                errors["installationAddress"] = ["Địa chỉ lắp đặt tối đa 200 ký tự."];

            if (!input.Latitude.HasValue || !input.Longitude.HasValue)
            {
                errors["coordinates"] = ["Vui lòng cung cấp cả vĩ độ (latitude) và kinh độ (longitude)."];
            }
            else
            {
                if (input.Latitude.Value is < -90.0 or > 90.0 || double.IsNaN(input.Latitude.Value) || double.IsInfinity(input.Latitude.Value))
                    errors["latitude"] = ["Vĩ độ (latitude) từ -90 đến 90."];
                if (input.Longitude.Value is < -180.0 or > 180.0 || double.IsNaN(input.Longitude.Value) || double.IsInfinity(input.Longitude.Value))
                    errors["longitude"] = ["Kinh độ (longitude) từ -180 đến 180."];
            }

            if (string.IsNullOrWhiteSpace(input.BankName) || input.BankName.Trim().Length is < 1 or > 120)
                errors["bankName"] = ["Tên ngân hàng từ 1 đến 120 ký tự."];
            if (string.IsNullOrWhiteSpace(input.BankHolder) || input.BankHolder.Trim().Length is < 1 or > 120)
                errors["bankHolder"] = ["Tên chủ tài khoản từ 1 đến 120 ký tự."];
            if (string.IsNullOrWhiteSpace(input.BankAccountNumber) || !SellerBankInput.ValidAccount(input.BankAccountNumber.Trim()))
                errors["bankAccountNumber"] = ["Số tài khoản từ 4 đến 34 chữ số hoặc chữ cái không dấu."];

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            var encryptedAccount = protection.CreateProtector("bank-account-v1").Protect(input.BankAccountNumber.Trim());

            await using var command = data.CreateCommand("SELECT flow.request_kiosk_application(@customer, @walk, @pre, @shop, @phone, @email, @address, @lat, @lon, @bank, @holder, @enc, @pricingVersion)");
            command.Parameters.AddWithValue("customer", actor);
            command.Parameters.AddWithValue("walk", input.WalkInSlots!.Value);
            command.Parameters.AddWithValue("pre", input.PreorderSlots!.Value);
            command.Parameters.AddWithValue("shop", input.ShopName.Trim());
            command.Parameters.AddWithValue("phone", input.Phone.Trim());
            command.Parameters.AddWithValue("email", input.ContactEmail.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("address", input.InstallationAddress.Trim());
            command.Parameters.AddWithValue("lat", input.Latitude!.Value);
            command.Parameters.AddWithValue("lon", input.Longitude!.Value);
            command.Parameters.AddWithValue("bank", input.BankName.Trim());
            command.Parameters.AddWithValue("holder", input.BankHolder.Trim().ToUpperInvariant());
            command.Parameters.AddWithValue("enc", encryptedAccount);
            command.Parameters.AddWithValue("pricingVersion", input.PricingVersion!.Value);

            Guid createdId;
            try
            {
                createdId = (Guid)(await command.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "22023")
            {
                return Results.Problem(statusCode: 400, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "55000")
            {
                return Results.Problem(statusCode: 503, detail: ex.MessageText);
            }

            var application = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == createdId, ct);
            var response = MapApplication(application, null, null, "PENDING", protection);
            return Results.Created($"/api/member/kiosk-applications/{createdId}", response);
        }).RequireAuthorization("PreorderCustomer").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>(201);

        // Existing Seller: request another whole kiosk under the same shop profile.
        app.MapPost("/api/me/kiosk-applications", async (
            CreateAdditionalKioskApplicationRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            FloraDbContext db,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            if (!ValidSlotDemand(input.WalkInSlots, input.PreorderSlots))
                return Results.Problem(statusCode: 400, detail: "Số ô Walk-in và Pre-order phải không âm, tổng từ 1 đến 50.");
            if (input.PricingVersion is null or <= 0)
                return Results.Problem(statusCode: 400, detail: "Cần gửi phiên bản bảng giá đã xem trước khi đăng ký.");
            if (string.IsNullOrWhiteSpace(input.InstallationAddress)
                || input.InstallationAddress.Trim().Length > 200
                || input.InstallationAddress.Any(char.IsControl))
                return Results.Problem(statusCode: 400, detail: "Địa chỉ lắp đặt tối đa 200 ký tự và không chứa ký tự điều khiển.");

            if (!input.Latitude.HasValue || !input.Longitude.HasValue
                || input.Latitude.Value is < -90 or > 90 || double.IsNaN(input.Latitude.Value) || double.IsInfinity(input.Latitude.Value)
                || input.Longitude.Value is < -180 or > 180 || double.IsNaN(input.Longitude.Value) || double.IsInfinity(input.Longitude.Value))
                return Results.Problem(statusCode: 400, detail: "Vui lòng cung cấp tọa độ latitude/longitude hợp lệ.");

            if (!Guid.TryParse(http.User.FindFirstValue("seller_id"), out var sellerId))
                return Results.Problem(statusCode: 403, detail: "Tài khoản chưa được gắn với Seller đang hoạt động.");

            await using var command = data.CreateCommand("SELECT flow.request_seller_kiosk_application(@user, @seller, @walk, @pre, @address, @lat, @lon, @pricingVersion)");
            command.Parameters.AddWithValue("user", Actor(http));
            command.Parameters.AddWithValue("seller", sellerId);
            command.Parameters.AddWithValue("walk", input.WalkInSlots!.Value);
            command.Parameters.AddWithValue("pre", input.PreorderSlots!.Value);
            command.Parameters.AddWithValue("address", input.InstallationAddress.Trim());
            command.Parameters.AddWithValue("lat", input.Latitude.Value);
            command.Parameters.AddWithValue("lon", input.Longitude.Value);
            command.Parameters.AddWithValue("pricingVersion", input.PricingVersion.Value);

            Guid createdId;
            try
            {
                createdId = (Guid)(await command.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "22023")
            {
                return Results.Problem(statusCode: 400, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "55000")
            {
                return Results.Problem(statusCode: 503, detail: ex.MessageText);
            }

            var application = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == createdId, ct);
            var response = MapApplication(application, null, null, "PENDING", protection);
            return Results.Created($"/api/member/kiosk-applications/{createdId}", response);
        }).RequireAuthorization("Seller").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>(201);

        // 2. Customer: Get current application
        app.MapGet("/api/member/kiosk-applications/current", async (
            HttpContext http,
            FloraDbContext db,
            IDataProtectionProvider protection,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking()
                .Where(x => x.CustomerId == actor)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (appRow is null) return Results.NotFound();

            var (techName, kioskCode) = await ResolveNames(data, appRow.AssignedTechnicianId, appRow.AssignedKioskId, ct);
            var payStatus = appRow.PaymentId.HasValue ? await ResolvePaymentStatus(data, appRow.PaymentId.Value, ct) : null;
            return Results.Ok(MapApplication(appRow, techName, kioskCode, payStatus, protection));
        }).RequireAuthorization("Member").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 3. Customer: Create payment link using immutable snapshot amount
        app.MapPost("/api/member/kiosk-applications/{id:guid}/payment-link", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            PayOsClient gateway,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null || !await OwnsApplicationAsync(db, http, appRow, ct)) return Results.NotFound();
            if (appRow.Status != "AWAITING_PAYMENT")
                return Results.Problem(statusCode: 409, detail: "Hồ sơ không ở trạng thái chờ thanh toán.");

            if (!gateway.IsConfigured)
                return Results.Problem(statusCode: 503, detail: "Cổng thanh toán payOS chưa được cấu hình.");

            if (!appRow.PaymentId.HasValue) return Results.Problem(statusCode: 500);

            await using var reserve = data.CreateCommand("SELECT flow.reserve_gateway_order(@id)");
            reserve.Parameters.AddWithValue("id", appRow.PaymentId.Value);
            var orderCode = (long)(await reserve.ExecuteScalarAsync(ct))!;

            var amount = appRow.AnnualFee.GetValueOrDefault(10000);

            try
            {
                var result = await PayOsPaymentLinks.CreateAsync(data, gateway, appRow.PaymentId.Value, orderCode, amount, ct);
                await using var save = data.CreateCommand("SELECT flow.set_payment_checkout_url(@id, @url, @actor, @appId)");
                save.Parameters.AddWithValue("url", result.CheckoutUrl);
                save.Parameters.AddWithValue("id", appRow.PaymentId.Value);
                save.Parameters.AddWithValue("actor", actor);
                save.Parameters.AddWithValue("appId", appRow.Id);
                await save.ExecuteNonQueryAsync(ct);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Problem(statusCode: 502, detail: "Không thể khởi tạo liên kết thanh toán với cổng payOS. Vui lòng thử lại.");
            }
        }).RequireAuthorization("KioskApplicationOwner").WithTags("KioskOnboarding").Produces<PaymentLinkResponse>();

        // 4. Customer: Cancel / Request refund
        app.MapPost("/api/member/kiosk-applications/{id:guid}/cancel", async (
            Guid id,
            CancelKioskApplicationRequest? input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null || !await OwnsApplicationAsync(db, http, appRow, ct)) return Results.NotFound();

            await using var command = data.CreateCommand("SELECT flow.kiosk_application_cancel(@id, @actor, @reason)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("reason", (object?)input?.Reason ?? DBNull.Value);

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Results.Ok(MapApplication(updated, null, null, null, protection));
        }).RequireAuthorization("KioskApplicationOwner").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 5. Customer / Admin: Owner-only server-to-server status reconciliation (allows late paid after cancel/reject)
        app.MapPost("/api/member/kiosk-applications/{id:guid}/reconcile", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            PayOsClient gateway,
            ICapPublisher publisher,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var role = http.User.FindFirstValue("role");
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null) return Results.NotFound();
            if (role != "ADMIN" && !await OwnsApplicationAsync(db, http, appRow, ct)) return Results.NotFound();

            if (appRow.Status != "AWAITING_PAYMENT" && appRow.Status != "CANCELLED" && appRow.Status != "REJECTED")
                return Results.Problem(statusCode: 409, detail: "Hồ sơ không ở trạng thái cần đối soát thanh toán.");

            if (!gateway.IsConfigured)
                return Results.Problem(statusCode: 503, detail: "Cổng thanh toán chưa được cấu hình.");

            if (!appRow.PaymentId.HasValue) return Results.Problem(statusCode: 500);

            await using var findOrder = data.CreateCommand("SELECT flow.get_payment_order_code(@id)");
            findOrder.Parameters.AddWithValue("id", appRow.PaymentId.Value);
            var codeObj = await findOrder.ExecuteScalarAsync(ct);
            if (codeObj is not long orderCode)
                return Results.Problem(statusCode: 409, detail: "Chưa có thông tin giao dịch cổng thanh toán.");

            var expectedAmount = appRow.AnnualFee.GetValueOrDefault(10000);

            JsonElement verified;
            try
            {
                verified = await gateway.GetPaymentRequestAsync(orderCode, ct);
            }
            catch (Exception ex) when (ex is PaymentGatewayException or HttpRequestException or JsonException)
            {
                return Results.Problem(statusCode: 502, detail: "Không thể tra cứu trạng thái giao dịch từ payOS.");
            }

            var status = verified.TryGetProperty("status", out var s) ? s.GetString() : null;
            var amount = verified.TryGetProperty("amount", out var a) && a.TryGetInt64(out var amt) ? amt : 0;
            var returnedOrderCode = verified.TryGetProperty("orderCode", out var oc) && oc.TryGetInt64(out var codeVal) ? codeVal : 0;

            if (status != "PAID")
                return Results.Problem(statusCode: 409, detail: "Giao dịch chưa được thanh toán thành công trên payOS.");

            if (returnedOrderCode != orderCode)
                return Results.Problem(statusCode: 409, detail: "Mã đơn hàng payOS không khớp.");

            if (amount != expectedAmount)
                return Results.Problem(statusCode: 409, detail: "Số tiền giao dịch payOS không khớp với gói thuê.");

            var reference = verified.TryGetProperty("reference", out var r) ? r.GetString() : ("RECONCILE-" + orderCode);

            await using var connection = await data.OpenConnectionAsync(ct);
            using var outbox = await connection.BeginTransactionAsync(publisher, cancellationToken: ct);
            var transaction = (NpgsqlTransaction)outbox.DbTransaction!;

            await using var checkCmd = new NpgsqlCommand("SELECT flow.get_payment_status(@id)", connection, transaction);
            checkCmd.Parameters.AddWithValue("id", appRow.PaymentId.Value);
            var currentPaymentStatus = (string?)await checkCmd.ExecuteScalarAsync(ct);
            var alreadySucceeded = currentPaymentStatus == "SUCCEEDED";

            await using var settle = new NpgsqlCommand("SELECT flow.kiosk_application_paid(@payment, @txn)", connection, transaction);
            settle.Parameters.AddWithValue("payment", appRow.PaymentId.Value);
            settle.Parameters.AddWithValue("txn", reference ?? ("PAYOS-" + orderCode));
            await settle.ExecuteNonQueryAsync(ct);

            if (!alreadySucceeded)
            {
                await PaymentEvents.PublishSucceededAsync(connection, transaction, publisher, appRow.PaymentId.Value, ct);
            }

            await outbox.CommitAsync(ct);
            await connection.CloseAsync();

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var (techName, kioskCode) = await ResolveNames(data, updated.AssignedTechnicianId, updated.AssignedKioskId, ct);
            return Results.Ok(MapApplication(updated, techName, kioskCode, "SUCCEEDED", protection));
        }).RequireAuthorization("KioskReconcile").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 6. Operations Manager: List applications (uses immutable snapshots)
        app.MapGet("/api/operations/kiosk-applications", async (
            string? status,
            int? page,
            int? limit,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var p = page.GetValueOrDefault(1);
            if (p < 1) p = 1;
            var l = Math.Clamp(limit.GetValueOrDefault(25), 1, 100);

            var query = db.KioskApplications.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(x => x.Status == status);

            query = query.OrderByDescending(x => x.CreatedAt);

            var rows = await query.Skip((p - 1) * l).Take(l + 1).ToListAsync(ct);
            var hasMore = rows.Count > l;
            var paged = rows.Take(l).ToList();

            var techIds = paged.Where(x => x.AssignedTechnicianId.HasValue).Select(x => x.AssignedTechnicianId!.Value).Distinct().ToArray();
            var techNames = await StaffIdentityRead.Users(data, techIds, ct);

            var kioskIds = paged.Where(x => x.AssignedKioskId.HasValue).Select(x => x.AssignedKioskId!.Value).Distinct().ToArray();
            var kioskCodes = await ResolveKioskCodes(data, kioskIds, ct);

            var items = paged.Select(x => MapApplication(
                x,
                x.AssignedTechnicianId.HasValue ? techNames.GetValueOrDefault(x.AssignedTechnicianId.Value) : null,
                x.AssignedKioskId.HasValue ? kioskCodes.GetValueOrDefault(x.AssignedKioskId.Value) : null,
                null,
                protection
            )).ToList();

            return Results.Ok(new KioskApplicationPage(items, p, hasMore));
        }).RequireAuthorization("OperationsManagerOrAdmin").WithTags("KioskOnboarding").Produces<KioskApplicationPage>();

        // 7. Operations Manager: Get application detail
        app.MapGet("/api/operations/kiosk-applications/{id:guid}", async (
            Guid id,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null) return Results.NotFound();

            var (techName, kioskCode) = await ResolveNames(data, appRow.AssignedTechnicianId, appRow.AssignedKioskId, ct);
            var payStatus = appRow.PaymentId.HasValue ? await ResolvePaymentStatus(data, appRow.PaymentId.Value, ct) : null;
            var mapped = MapApplication(appRow, techName, kioskCode, payStatus, protection);

            var evidence = await ReadEvidencePhotos(data, id, ct);
            return Results.Ok(new KioskApplicationDetailResponse(mapped, evidence));
        }).RequireAuthorization("OperationsManagerOrAdmin").WithTags("KioskOnboarding").Produces<KioskApplicationDetailResponse>();

        // 8. Operations Manager: Confirm application contact (atomic SQL flow)
        app.MapPost("/api/operations/kiosk-applications/{id:guid}/confirm", async (
            Guid id,
            ConfirmKioskApplicationRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Note) || input.Note.Trim().Length < 5)
                return Results.Problem(statusCode: 400, detail: "Ghi chú xác nhận liên hệ tối thiểu 5 ký tự.");

            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_confirm(@id, @actor, @note)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("note", input.Note.Trim());

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState is "P0001" or "22023" or "42501")
            {
                return Results.Problem(statusCode: ex.SqlState == "42501" ? 403 : ex.SqlState == "22023" ? 400 : 409, detail: ex.MessageText);
            }

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var status = updated.PaymentId.HasValue ? await ResolvePaymentStatus(data, updated.PaymentId.Value, ct) : null;
            return Results.Ok(MapApplication(updated, null, null, status, protection));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 9. Operations Manager: Schedule technician and assign kiosk (atomic SQL flow)
        app.MapPost("/api/operations/kiosk-applications/{id:guid}/schedule", async (
            Guid id,
            ScheduleKioskApplicationRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            if (string.IsNullOrWhiteSpace(input.TimeWindow) || !TimeWindowRegex().IsMatch(input.TimeWindow.Trim()))
                return Results.Problem(statusCode: 400, detail: "Khung giờ không hợp lệ (yêu cầu định dạng HH:mm - HH:mm).");

            var match = TimeWindowRegex().Match(input.TimeWindow.Trim());
            var start = TimeOnly.Parse(match.Groups["start"].Value);
            var end = TimeOnly.Parse(match.Groups["end"].Value);
            if (start >= end)
                return Results.Problem(statusCode: 400, detail: "Khung giờ không hợp lệ (giờ kết thúc phải sau giờ bắt đầu).");

            await using var command = data.CreateCommand("SELECT flow.kiosk_application_schedule(@id, @actor, @tech, @kiosk, @date, @window)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("tech", input.TechnicianId);
            command.Parameters.AddWithValue("kiosk", input.KioskId);
            command.Parameters.AddWithValue("date", input.ScheduledDate);
            command.Parameters.AddWithValue("window", input.TimeWindow.Trim());

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "22023")
            {
                return Results.Problem(statusCode: 400, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var (techName, kioskCode) = await ResolveNames(data, updated.AssignedTechnicianId, updated.AssignedKioskId, ct);
            return Results.Ok(MapApplication(updated, techName, kioskCode, "SUCCEEDED", protection));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 10. Operations Manager: Require rework (atomic SQL flow)
        app.MapPost("/api/operations/kiosk-applications/{id:guid}/rework", async (
            Guid id,
            ReworkKioskApplicationRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length < 5)
                return Results.Problem(statusCode: 400, detail: "Lý do yêu cầu làm lại tối thiểu 5 ký tự.");

            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_rework(@id, @actor, @reason)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("reason", input.Reason.Trim());

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var (techName, kioskCode) = await ResolveNames(data, updated.AssignedTechnicianId, updated.AssignedKioskId, ct);
            return Results.Ok(MapApplication(updated, techName, kioskCode, "SUCCEEDED", protection));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 11. Operations Manager: Complete handover (atomic SQL flow)
        app.MapPost("/api/operations/kiosk-applications/{id:guid}/handover", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_handover(@id, @manager)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("manager", actor);

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var (techName, kioskCode) = await ResolveNames(data, updated.AssignedTechnicianId, updated.AssignedKioskId, ct);
            return Results.Ok(MapApplication(updated, techName, kioskCode, "SUCCEEDED", protection));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 12. Operations Manager: Reject application (atomic SQL flow)
        app.MapPost("/api/operations/kiosk-applications/{id:guid}/reject", async (
            Guid id,
            RejectKioskApplicationRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length < 5)
                return Results.Problem(statusCode: 400, detail: "Lý do từ chối tối thiểu 5 ký tự.");

            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_reject(@id, @actor, @reason)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("reason", input.Reason.Trim());

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Results.Ok(MapApplication(updated, null, null, null, protection));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 13. Operations Manager: Platform failure remediation (atomic SQL flow)
        app.MapPost("/api/operations/kiosk-applications/{id:guid}/platform-failure", async (
            Guid id,
            PlatformFailureRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Reason) || input.Reason.Trim().Length < 5)
                return Results.Problem(statusCode: 400, detail: "Lý do sự cố tối thiểu 5 ký tự.");

            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_platform_failure(@id, @actor, @reason)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("reason", input.Reason.Trim());

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            return Results.Ok(MapApplication(updated, null, null, null, protection));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<KioskApplicationResponse>();

        // 14. Operations Manager: List eligible kiosks
        app.MapGet("/api/operations/eligible-kiosks", async (
            Guid? packageId,
            Guid? applicationId,
            FloraDbContext db,
            CancellationToken ct) =>
        {
            int? requiredTotal = null;
            int? requiredWalk = null, requiredPre = null;
            if (applicationId.HasValue)
            {
                var application = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == applicationId.Value, ct);
                if (application is null) return Results.NotFound();
                requiredTotal = application.TotalSlots;
                if (application.PricingMode == "SLOT_PRICING")
                {
                    requiredWalk = application.WalkInSlots;
                    requiredPre = application.PreorderSlots;
                }
            }
            else if (packageId.HasValue)
            {
                var pkg = await OnboardingPackages.FindActiveAnnualPackageAsync(db, packageId.Value, ct);
                if (pkg is not null)
                    requiredTotal = pkg.TotalSlots;
            }

            var reservedKioskIds = await db.KioskApplications.AsNoTracking()
                .Where(x => (x.Status == "SCHEDULED" || x.Status == "INSTALLING" || x.Status == "AWAITING_HANDOVER" || x.Status == "ACTIVE") && x.AssignedKioskId.HasValue && x.Id != applicationId)
                .Select(x => x.AssignedKioskId!.Value)
                .ToListAsync(ct);

            var kiosks = await db.Kiosks.AsNoTracking()
                .Where(x => x.SellerId == null && x.Status != "DISABLED" && x.Status != "MAINTENANCE" && !reservedKioskIds.Contains(x.Id))
                .Where(x => x.RequestedApplicationId == null || x.RequestedApplicationId == applicationId)
                .Select(x => new
                {
                    x.Id,
                    x.Code,
                    x.Name,
                    x.Status,
                    SlotCount = x.Slots.Count,
                    WalkInSlots = x.Slots.Count(s => s.Zone == "WALK_IN"),
                    PreorderSlots = x.Slots.Count(s => s.Zone == "PREORDER"),
                    UnclassifiedSlots = x.Slots.Count(s => s.Zone == null),
                    HasUnavailableSlot = x.Slots.Any(s => s.CurrentSellerId.HasValue || s.Status != "FREE" || s.BouquetId.HasValue || s.HoldUntil.HasValue || s.HoldOrderId.HasValue)
                })
                .Where(x => !x.HasUnavailableSlot)
                .ToListAsync(ct);

            if (requiredTotal.HasValue)
            {
                kiosks = kiosks.Where(x => x.SlotCount == requiredTotal.Value).ToList();
            }
            if (requiredWalk.HasValue)
                kiosks = kiosks.Where(x => x.WalkInSlots == requiredWalk && x.PreorderSlots == requiredPre && x.UnclassifiedSlots == 0).ToList();

            return Results.Ok(kiosks.Select(x => new EligibleKioskResponse(x.Id, x.Code, x.Name, x.SlotCount, x.Status,
                x.WalkInSlots, x.PreorderSlots, x.UnclassifiedSlots)).ToList());
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<List<EligibleKioskResponse>>();

        // 15. Operations Manager: Enroll new kiosk with once-only API key response
        app.MapPost("/api/operations/kiosks/enroll", async (
            EnrollKioskRequest input,
            HttpContext http,
            NpgsqlDataSource data,
            FloraDbContext db,
            Esp32ProvisioningStore provisioning,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Actor(http);
            if (string.IsNullOrWhiteSpace(input.Code) || input.Code.Trim().Length is < 2 or > 50)
                return Results.Problem(statusCode: 400, detail: "Mã kiosk từ 2 đến 50 ký tự.");
            if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length is < 2 or > 120)
                return Results.Problem(statusCode: 400, detail: "Tên kiosk từ 2 đến 120 ký tự.");
            if (string.IsNullOrWhiteSpace(input.HardwareId) || input.HardwareId.Trim().Length < 2)
                return Results.Problem(statusCode: 400, detail: "HardwareId không được để trống.");
            var hardware = input.HardwareId.Trim();
            var mqttClient = input.MqttClientId?.Trim();
            if (provisioning.Enabled)
            {
                if (!DeviceProtocol.ValidHardwareId(hardware))
                    return Results.Problem(statusCode: 400, detail: "HardwareId chỉ gồm chữ, số, dấu gạch ngang hoặc gạch dưới, dài 2–64 ký tự.");
                var firmwareClient = "esp32-" + hardware;
                if (!string.IsNullOrEmpty(mqttClient) && mqttClient != firmwareClient)
                    return Results.Problem(statusCode: 400, detail: "MQTT client ID phải là esp32-<HardwareId>; có thể bỏ trống để hệ thống tự điền.");
                mqttClient = firmwareClient;
            }
            if (!input.ApplicationId.HasValue || input.ApplicationId == Guid.Empty)
                return Results.Problem(statusCode: 400, detail: "Cần applicationId của hồ sơ thuê để tạo kiosk đúng nhu cầu.");
            var application = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ApplicationId.Value, ct);
            if (application is null) return Results.NotFound();
            if (input.SlotCount.HasValue && input.SlotCount != application.TotalSlots)
                return Results.Problem(statusCode: 400, detail: "Tổng số ô phải khớp hồ sơ thuê.");

            var rawApiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawApiKey)));

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand("SELECT flow.enroll_requested_kiosk(@application, @code, @name, @hw, @mqtt, @region, @keyHash, @actor)", connection, transaction);
            command.Parameters.AddWithValue("code", input.Code.Trim());
            command.Parameters.AddWithValue("name", input.Name.Trim());
            command.Parameters.AddWithValue("hw", hardware);
            command.Parameters.AddWithValue("mqtt", (object?)mqttClient ?? DBNull.Value);
            command.Parameters.AddWithValue("region", (object?)input.Region?.Trim() ?? "HCM");
            command.Parameters.AddWithValue("application", application.Id);
            command.Parameters.AddWithValue("keyHash", keyHash);
            command.Parameters.AddWithValue("actor", actor);

            Guid kioskId;
            try
            {
                kioskId = (Guid)(await command.ExecuteScalarAsync(ct))!;
                if (provisioning.Enabled)
                {
                    await using var assignment = new NpgsqlCommand("""
                        INSERT INTO kiosk_ops.manager_kiosks(manager_id,kiosk_id) VALUES(@actor,@kiosk);
                        SELECT flow.audit(@actor,'KIOSK_MANAGER_AUTO_ASSIGNED','kiosks',@kiosk,'{}'::jsonb);
                        """, connection, transaction);
                    assignment.Parameters.AddWithValue("actor", actor);
                    assignment.Parameters.AddWithValue("kiosk", kioskId);
                    await assignment.ExecuteNonQueryAsync(ct);
                    await provisioning.EnqueueAsync(connection, transaction, kioskId, actor, ct);
                }
                await transaction.CommitAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Problem(statusCode: 409, detail: "Mã kiosk hoặc hardware ID đã tồn tại.");
            }
            catch (PostgresException ex) when (ex.SqlState is "P0001" or "22023" or "42501")
            {
                return Results.Problem(statusCode: ex.SqlState == "42501" ? 403 : 409, detail: ex.MessageText);
            }

            application = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == application.Id, ct);
            return Results.Created($"/api/kiosks/{kioskId}", new EnrollKioskResponse(kioskId, input.Code.Trim(), input.Name.Trim(),
                application.TotalSlots!.Value, "OFFLINE", rawApiKey, application.WalkInSlots!.Value, application.PreorderSlots!.Value, application.Id,
                provisioning.Enabled ? new Esp32ProvisioningStatus("PENDING", null, 0, null) : null));
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<EnrollKioskResponse>(201)
          .WithDescription("Kiosk được tạo OFFLINE. Khi bật tự cấp ESP32, yêu cầu cấp MQTT/HMAC được lưu cùng transaction; provisioning PENDING/PROVISIONING/READY/FAILED tách biệt trạng thái ONLINE. Không trả bí mật ESP32 trong response đăng ký.");

        // Operations Manager: archive an offline kiosk after its rental, stock and pending work have ended.
        app.MapDelete("/api/operations/kiosks/{id:guid}", async (
            Guid id,
            HttpContext http,
            NpgsqlDataSource data,
            IMqttDeviceProvisioner mqttProvisioner,
            IServiceProvider services,
            CancellationToken ct) =>
        {
            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            await using var command = new NpgsqlCommand("SELECT flow.archive_kiosk(@id, @actor)", connection, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", Actor(http));

            try
            {
                // Serialize against retries/creation. NOWAIT on the job prevents a lock cycle
                // against a worker claiming that job while it resolves the kiosk identity.
                await using (var kioskLock = new NpgsqlCommand("SELECT id FROM kiosk_ops.kiosks WHERE id=@id FOR UPDATE", connection, transaction))
                {
                    kioskLock.Parameters.AddWithValue("id", id);
                    await kioskLock.ExecuteScalarAsync(ct);
                }
                string? provisionedHardware = null;
                await using (var job = new NpgsqlCommand("SELECT hardware_id,state FROM kiosk_ops.esp32_provisioning_jobs WHERE kiosk_id=@id FOR UPDATE NOWAIT", connection, transaction))
                {
                    job.Parameters.AddWithValue("id", id);
                    await using var reader = await job.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        if (reader.GetString(1) == "PROVISIONING")
                            return Results.Problem(statusCode: 409, detail: "Thiết bị đang được cấp cấu hình. Hãy chờ hoàn tất rồi thử lưu trữ lại.");
                        provisionedHardware = reader.GetString(0);
                    }
                }
                var archived = (bool)(await command.ExecuteScalarAsync(ct))!;
                if (archived && provisionedHardware is not null)
                    await mqttProvisioner.DisableAsync(id, provisionedHardware, ct);
                await transaction.CommitAsync(ct);
                if (archived && provisionedHardware is not null)
                    services.GetService<DeviceKeyRing>()?.RemoveDynamic(provisionedHardware);
                return archived
                    ? Results.NoContent()
                    : Results.NotFound(new ProblemDetails { Title = "Không tìm thấy", Detail = "Kiosk không tồn tại." });
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(
                    statusCode: 409,
                    title: "Kiosk chưa đủ điều kiện lưu trữ",
                    detail: ex.MessageText switch
                    {
                        "KIOSK_ARCHIVE_MUST_BE_OFFLINE" => "Chỉ lưu trữ được kiosk đang OFFLINE.",
                        "KIOSK_ARCHIVE_SELLER_STILL_ASSIGNED" => "Kiosk vẫn còn Seller hoặc ô đang được gán. Hãy hoàn tất chấm dứt hợp đồng trước.",
                        "KIOSK_ARCHIVE_SLOTS_NOT_EMPTY" => "Kiosk còn hoa, đơn giữ hoặc ô đang cần xử lý. Hãy làm trống và giải phóng tất cả ô trước.",
                        "KIOSK_ARCHIVE_LEASE_STILL_ACTIVE" => "Kiosk còn kỳ thuê đang hoạt động hoặc chờ thanh toán.",
                        "KIOSK_ARCHIVE_PENDING_PAYMENT" => "Kiosk còn khoản thanh toán thuê đang chờ xử lý.",
                        "KIOSK_ARCHIVE_ONBOARDING_IN_PROGRESS" => "Kiosk còn hồ sơ đăng ký hoặc lắp đặt đang xử lý.",
                        "KIOSK_ARCHIVE_TERMINATION_IN_PROGRESS" => "Yêu cầu chấm dứt hợp đồng của kiosk chưa hoàn tất.",
                        "KIOSK_ARCHIVE_ACCESSORY_STOCK_REMAINS" => "Kiosk vẫn còn phụ kiện trong kho.",
                        "KIOSK_ARCHIVE_PENDING_WORK" => "Kiosk còn sự cố hoặc công việc nhân viên chưa hoàn tất.",
                        "KIOSK_ARCHIVE_PENDING_ORDER" => "Kiosk còn đơn hàng, đơn đặt trước hoặc thanh toán đang chờ.",
                        "KIOSK_ARCHIVE_DEVICE_COMMAND_PENDING" => "Kiosk còn lệnh mở cửa đang xử lý.",
                        _ => "Hãy xử lý các hợp đồng, hàng tồn, đơn hàng và công việc chưa hoàn tất rồi thử lại."
                    });
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, title: "Không có quyền lưu trữ kiosk",
                    detail: "Chỉ Operations Manager đang hoạt động mới được lưu trữ kiosk.");
            }
            catch (PostgresException ex) when (ex.SqlState is "55P03" or "40P01")
            {
                return Results.Problem(statusCode: 409, detail: "Kiosk đang được xử lý. Hãy tải lại rồi thử lưu trữ lại.");
            }
            catch (Esp32ProvisioningException ex)
            {
                return Results.Problem(statusCode: 409, title: "Chưa thu hồi được tài khoản MQTT của kiosk",
                    extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
            }
        })
        .RequireAuthorization("OperationsManager")
        .WithTags("KioskOnboarding")
        .WithSummary("Lưu trữ kiosk OFFLINE đã kết thúc thuê")
        .WithDescription("Lưu trữ mềm: chỉ Operations Manager được thao tác khi kiosk OFFLINE, không còn Seller/lease, tồn kho, đơn hàng hoặc công việc đang chờ. Kiosk được chuyển sang DISABLED; dữ liệu, đơn hàng, thanh toán và nhật ký lịch sử được giữ nguyên. Trả 409 nếu còn điều kiện chưa hoàn tất.")
        .Produces(204)
        .ProducesProblem(401)
        .ProducesProblem(403)
        .ProducesProblem(404)
        .ProducesProblem(409);

        // 15b. Operations Manager: Provision / Rotate kiosk key (once-only no-store response)
        app.MapPost("/api/operations/kiosks/{id:guid}/provision-key", async (
            Guid id,
            HttpContext http,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Actor(http);
            var rawApiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawApiKey)));

            await using var command = data.CreateCommand("SELECT flow.kiosk_provision_key(@id, @keyHash, @actor)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("keyHash", keyHash);
            command.Parameters.AddWithValue("actor", actor);

            try
            {
                await command.ExecuteScalarAsync(ct);
                return Results.Ok(new ProvisionKioskKeyResponse(id, rawApiKey));
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
        }).RequireAuthorization("OperationsManager").WithTags("KioskOnboarding").Produces<ProvisionKioskKeyResponse>();

        // 16. Technician: List assigned installation jobs, including handed-over history
        app.MapGet("/api/staff/kiosk-installations", async (
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var list = await db.KioskApplications.AsNoTracking()
                .Where(x => x.AssignedTechnicianId == actor && (x.Status == "SCHEDULED" || x.Status == "INSTALLING" || x.Status == "AWAITING_HANDOVER" || x.Status == "ACTIVE"))
                .OrderBy(x => x.ScheduledDate).ThenBy(x => x.Id)
                .ToListAsync(ct);

            var kioskIds = list.Where(x => x.AssignedKioskId.HasValue).Select(x => x.AssignedKioskId!.Value).Distinct().ToArray();
            var kioskCodes = await ResolveKioskCodes(data, kioskIds, ct);

            var items = list.Select(x => new TechnicianInstallationSummary(
                x.Id,
                x.ShopName,
                x.Phone,
                x.ContactEmail,
                x.InstallationAddress,
                x.Latitude,
                x.Longitude,
                x.AssignedKioskId.HasValue ? kioskCodes.GetValueOrDefault(x.AssignedKioskId.Value) : null,
                x.ScheduledDate,
                x.TimeWindow,
                x.Status,
                x.ReworkReason
            )).ToList();

            return Results.Ok(items);
        }).RequireAuthorization("Technician").WithTags("KioskOnboarding").Produces<List<TechnicianInstallationSummary>>();

        // 17. Technician: Get installation job detail
        app.MapGet("/api/staff/kiosk-installations/{id:guid}", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null || appRow.AssignedTechnicianId != actor) return Results.NotFound();

            var kioskCode = appRow.AssignedKioskId.HasValue
                ? (await ResolveKioskCodes(data, [appRow.AssignedKioskId.Value], ct)).GetValueOrDefault(appRow.AssignedKioskId.Value)
                : null;

            var evidence = await ReadEvidenceItems(data, id, ct);

            return Results.Ok(new TechnicianInstallationDetail(
                appRow.Id,
                appRow.ShopName,
                appRow.Phone,
                appRow.ContactEmail,
                appRow.InstallationAddress,
                appRow.Latitude,
                appRow.Longitude,
                appRow.AssignedKioskId,
                kioskCode,
                appRow.ScheduledDate,
                appRow.TimeWindow,
                appRow.Status,
                appRow.TechnicianReport,
                appRow.ReworkReason,
                evidence
            ));
        }).RequireAuthorization("Technician").WithTags("KioskOnboarding").Produces<TechnicianInstallationDetail>();

        // 18. Technician: Start installation visit (atomic SQL flow)
        app.MapPost("/api/staff/kiosk-installations/{id:guid}/start", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_start(@id, @actor)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            var appRow = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var kioskCode = appRow.AssignedKioskId.HasValue
                ? (await ResolveKioskCodes(data, [appRow.AssignedKioskId.Value], ct)).GetValueOrDefault(appRow.AssignedKioskId.Value)
                : null;

            return Results.Ok(new TechnicianInstallationDetail(
                appRow.Id, appRow.ShopName, appRow.Phone, appRow.ContactEmail, appRow.InstallationAddress,
                appRow.Latitude, appRow.Longitude, appRow.AssignedKioskId, kioskCode,
                appRow.ScheduledDate, appRow.TimeWindow, appRow.Status, appRow.TechnicianReport, appRow.ReworkReason, []
            ));
        }).RequireAuthorization("Technician").WithTags("KioskOnboarding").Produces<TechnicianInstallationDetail>();

        // 19. Technician: Upload private installation photo evidence
        PrivateEvidence.DescribeUpload(app.MapPost("/api/staff/kiosk-installations/{id:guid}/evidence/upload", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            CloudinaryMedia media,
            PrivateEvidence tickets,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null || appRow.AssignedTechnicianId != actor) return Results.NotFound();
            if (appRow.Status != "INSTALLING")
                return Results.Problem(statusCode: 409, detail: "Chỉ tải ảnh khi công việc đang thực hiện (INSTALLING).");

            return await PrivateEvidence.UploadAsync(http, media, tickets, "kiosk_installation", TechEvidenceBinding(actor, id), ct);
        }).RequireAuthorization("Technician").WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)).Produces<EvidenceUploadResponse>());

        // 20. Technician: Attach uploaded evidence ticket
        app.MapPost("/api/staff/kiosk-installations/{id:guid}/evidence", async (
            Guid id,
            AttachKioskEvidenceRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            PrivateEvidence tickets,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null || appRow.AssignedTechnicianId != actor) return Results.NotFound();
            if (appRow.Status != "INSTALLING")
                return Results.Problem(statusCode: 409, detail: "Chỉ đính kèm ảnh khi công việc đang thực hiện (INSTALLING).");

            VerifiedMedia verified;
            try
            {
                verified = tickets.Resolve(input.Reference, "kiosk_installation", TechEvidenceBinding(actor, id));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: 400, detail: ex.Message);
            }

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            if (!await PrivateEvidence.LockUnusedAsync(connection, transaction, verified.Url, ct))
                return Results.Problem(statusCode: 409, detail: "Ảnh đã được gắn vào hồ sơ.");

            await using var insert = new NpgsqlCommand("SELECT flow.attach_kiosk_evidence(@id, @appId, @url, @mime, @size, @hash, @actor)", connection, transaction);
            insert.Parameters.AddWithValue("id", input.Id);
            insert.Parameters.AddWithValue("appId", id);
            insert.Parameters.AddWithValue("url", verified.Url);
            insert.Parameters.AddWithValue("mime", verified.MimeType);
            insert.Parameters.AddWithValue("size", verified.SizeBytes);
            insert.Parameters.AddWithValue("hash", verified.Sha256);
            insert.Parameters.AddWithValue("actor", actor);

            DateTime created;
            try
            {
                created = (DateTime)(await insert.ExecuteScalarAsync(ct))!;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                return Results.Conflict();
            }
            catch (PostgresException ex) when (ex.SqlState == "P0002")
            {
                return Results.NotFound();
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            await transaction.CommitAsync(ct);
            return Results.Ok(new StaffEvidenceItem(input.Id, created));
        }).RequireAuthorization("Technician").WithTags("KioskOnboarding").Produces<StaffEvidenceItem>();

        // 21. Technician: List evidence
        app.MapGet("/api/staff/kiosk-installations/{id:guid}/evidence", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null || appRow.AssignedTechnicianId != actor) return Results.NotFound();

            var items = await ReadEvidenceItems(data, id, ct);
            return Results.Ok(items);
        }).RequireAuthorization("Technician").WithTags("KioskOnboarding").Produces<List<StaffEvidenceItem>>();

        // 21b. Technician: Private binary fetch for installation evidence
        app.MapGet("/api/staff/kiosk-installations/{id:guid}/evidence/{attachmentId:guid}", async (
            Guid id,
            Guid attachmentId,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CloudinaryMedia media,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null || appRow.AssignedTechnicianId != actor) return Results.NotFound();

            return await ServeAttachmentFile(data, media, id, attachmentId, "EVIDENCE", ct);
        }).RequireAuthorization("Technician").WithTags("KioskOnboarding");

        // 21c. Operations Manager / Admin: Private binary fetch for installation evidence
        app.MapGet("/api/operations/kiosk-applications/{id:guid}/evidence/{attachmentId:guid}", async (
            Guid id,
            Guid attachmentId,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CloudinaryMedia media,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null) return Results.NotFound();

            return await ServeAttachmentFile(data, media, id, attachmentId, "EVIDENCE", ct);
        }).RequireAuthorization("OperationsManagerOrAdmin").WithTags("KioskOnboarding");

        // 22. Technician: Submit installation report (atomic SQL flow)
        app.MapPost("/api/staff/kiosk-installations/{id:guid}/submit", async (
            Guid id,
            SubmitInstallationReportRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Report) || input.Report.Trim().Length < 10)
                return Results.Problem(statusCode: 400, detail: "Báo cáo nghiệm thu lắp đặt tối thiểu 10 ký tự.");

            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_submit(@id, @actor, @report)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("report", input.Report.Trim());

            try
            {
                await command.ExecuteScalarAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "42501")
            {
                return Results.Problem(statusCode: 403, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }

            var appRow = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == id, ct);
            var kioskCode = appRow.AssignedKioskId.HasValue
                ? (await ResolveKioskCodes(data, [appRow.AssignedKioskId.Value], ct)).GetValueOrDefault(appRow.AssignedKioskId.Value)
                : null;

            return Results.Ok(new TechnicianInstallationDetail(
                appRow.Id, appRow.ShopName, appRow.Phone, appRow.ContactEmail, appRow.InstallationAddress,
                appRow.Latitude, appRow.Longitude, appRow.AssignedKioskId, kioskCode,
                appRow.ScheduledDate, appRow.TimeWindow, appRow.Status, appRow.TechnicianReport, appRow.ReworkReason, []
            ));
        }).RequireAuthorization("Technician").WithTags("KioskOnboarding").Produces<TechnicianInstallationDetail>();

        // 23. Admin: List refund-pending kiosk applications
        app.MapGet("/api/admin/kiosk-applications/refunds", async (
            FloraDbContext db,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            var apps = await db.KioskApplications.AsNoTracking()
                .Where(x => x.Status == "REFUND_PENDING")
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(ct);

            var items = apps.Select(x => new AdminKioskRefundSummary(
                x.Id,
                x.ShopName,
                x.AnnualFee.GetValueOrDefault(10000),
                x.BankName,
                x.BankHolder,
                MaskAccount(x.BankAccountNoEnc, protection),
                x.ReviewNote ?? x.ReworkReason,
                x.Status,
                x.RefundApprovedBy,
                x.CreatedAt
            )).ToList();

            return Results.Ok(items);
        }).RequireAuthorization("Admin").WithTags("KioskOnboarding").Produces<List<AdminKioskRefundSummary>>();

        // 23b. Admin: Reveal recipient actual bank account for manual transfer (audited, no-store, separation of duty)
        app.MapPost("/api/admin/kiosk-applications/{id:guid}/refund/bank-details", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            IDataProtectionProvider protection,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actor = Actor(http);

            await using var checkAdmin = data.CreateCommand("SELECT flow.require_active_admin(@actor)");
            checkAdmin.Parameters.AddWithValue("actor", actor);
            await checkAdmin.ExecuteNonQueryAsync(ct);

            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null) return Results.NotFound();
            if (appRow.Status != "REFUND_PENDING")
                return Results.Problem(statusCode: 409, detail: "Khoản hoàn không ở trạng thái chờ chi.");

            if (!appRow.RefundApprovedBy.HasValue)
                return Results.Problem(statusCode: 409, detail: "Khoản hoàn cần được quản trị viên duyệt trước khi chi.");

            if (appRow.RefundApprovedBy.Value == actor)
                return Results.Problem(statusCode: 409, detail: "Người chi tiền phải khác người duyệt hoàn.");

            string clearAccount;
            try
            {
                clearAccount = protection.CreateProtector("bank-account-v1").Unprotect(appRow.BankAccountNoEnc);
            }
            catch (CryptographicException)
            {
                return Results.Problem(statusCode: 500, detail: "Không thể giải mã số tài khoản ngân hàng.");
            }

            await using var audit = data.CreateCommand("SELECT flow.audit(@actor, 'KIOSK_REFUND_BANK_REVEALED', 'kiosk_applications', @id)");
            audit.Parameters.AddWithValue("actor", actor);
            audit.Parameters.AddWithValue("id", id);
            await audit.ExecuteNonQueryAsync(ct);

            return Results.Ok(new KioskRefundBankResponse(appRow.BankName, appRow.BankHolder, clearAccount));
        }).RequireAuthorization("Admin").WithTags("KioskOnboarding").Produces<KioskRefundBankResponse>();

        // 23c. Admin: Upload manual refund transfer proof
        PrivateEvidence.DescribeUpload(app.MapPost("/api/admin/kiosk-applications/{id:guid}/refund/evidence/upload", async (
            Guid id,
            HttpContext http,
            FloraDbContext db,
            CloudinaryMedia media,
            PrivateEvidence tickets,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null) return Results.NotFound();
            if (appRow.Status != "REFUND_PENDING")
                return Results.Problem(statusCode: 409, detail: "Chỉ tải chứng từ khi khoản hoàn đang chờ chi.");

            return await PrivateEvidence.UploadAsync(http, media, tickets, "confirm_refund", PrivateEvidence.AdminBinding(actor.ToString(), id), ct);
        }).RequireAuthorization("Admin").WithMetadata(new RequestSizeLimitAttribute(CloudinaryMedia.MaximumBytes)).Produces<EvidenceUploadResponse>());

        // 23d. Admin: Fetch refund payout proof image
        app.MapGet("/api/admin/kiosk-applications/{id:guid}/refund/proof/{attachmentId:guid}", async (
            Guid id,
            Guid attachmentId,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            CloudinaryMedia media,
            CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null) return Results.NotFound();

            return await ServeAttachmentFile(data, media, id, attachmentId, "PROOF", ct);
        }).RequireAuthorization("Admin").WithTags("KioskOnboarding");

        // 24. Admin: Approve refund
        app.MapPost("/api/admin/kiosk-applications/{id:guid}/refund/approve", async (
            Guid id,
            HttpContext http,
            NpgsqlDataSource data,
            CancellationToken ct) =>
        {
            var actor = Actor(http);
            await using var command = data.CreateCommand("SELECT flow.kiosk_application_refund_approve(@id, @actor)");
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);

            try
            {
                await command.ExecuteScalarAsync(ct);
                return Results.Ok(new { success = true });
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
        }).RequireAuthorization("Admin").WithTags("KioskOnboarding");

        // 25. Admin: Confirm refund payout with proof reference
        app.MapPost("/api/admin/kiosk-applications/{id:guid}/refund/confirm", async (
            Guid id,
            ConfirmKioskRefundRequest input,
            HttpContext http,
            FloraDbContext db,
            NpgsqlDataSource data,
            PrivateEvidence tickets,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Reference) || input.Reference.Trim().Length < 3)
                return Results.Problem(statusCode: 400, detail: "Mã tham chiếu chuyển khoản tối thiểu 3 ký tự.");
            if (string.IsNullOrWhiteSpace(input.EvidenceReference))
                return Results.Problem(statusCode: 400, detail: "Chứng từ chuyển khoản không được để trống.");

            var actor = Actor(http);
            VerifiedMedia verified;
            try
            {
                verified = tickets.Resolve(input.EvidenceReference, "confirm_refund", PrivateEvidence.AdminBinding(actor.ToString(), id));
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(statusCode: 400, detail: ex.Message);
            }

            var appRow = await db.KioskApplications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (appRow is null) return Results.NotFound();
            if (appRow.Status == "REFUNDED")
            {
                if (appRow.RefundReference == input.Reference.Trim())
                    return Results.Ok(new { success = true });
                return Results.Problem(statusCode: 409, detail: "Khoản hoàn đã được chi với mã tham chiếu khác.");
            }
            if (appRow.Status != "REFUND_PENDING")
                return Results.Problem(statusCode: 409, detail: "Đơn đăng ký không ở trạng thái chờ hoàn tiền.");

            await using var connection = await data.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            if (!await PrivateEvidence.LockUnusedAsync(connection, transaction, verified.Url, ct))
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: 409, detail: "Chứng từ chuyển khoản đã được sử dụng.");
            }

            var attachmentId = Guid.CreateVersion7();
            await using var insertProof = new NpgsqlCommand("SELECT flow.attach_kiosk_refund_proof(@attId, @appId, @url, @mime, @size, @hash, @actor)", connection, transaction);
            insertProof.Parameters.AddWithValue("attId", attachmentId);
            insertProof.Parameters.AddWithValue("appId", id);
            insertProof.Parameters.AddWithValue("url", verified.Url);
            insertProof.Parameters.AddWithValue("mime", verified.MimeType);
            insertProof.Parameters.AddWithValue("size", verified.SizeBytes);
            insertProof.Parameters.AddWithValue("hash", verified.Sha256);
            insertProof.Parameters.AddWithValue("actor", actor);
            await insertProof.ExecuteNonQueryAsync(ct);

            await using var command = new NpgsqlCommand("SELECT flow.kiosk_application_refund_confirm(@id, @actor, @reference, @attachment)", connection, transaction);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("actor", actor);
            command.Parameters.AddWithValue("reference", input.Reference.Trim());
            command.Parameters.AddWithValue("attachment", attachmentId);

            try
            {
                await command.ExecuteScalarAsync(ct);
                await transaction.CommitAsync(ct);
                return Results.Ok(new { success = true });
            }
            catch (PostgresException ex) when (ex.SqlState == "P0001")
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: 409, detail: ex.MessageText);
            }
            catch (PostgresException ex) when (ex.SqlState == "22023")
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: 400, detail: ex.MessageText);
            }
        }).RequireAuthorization("Admin").WithTags("KioskOnboarding");
    }

    private static KioskApplicationResponse MapApplication(
        KioskApplication row,
        string? technicianName,
        string? kioskCode,
        string? paymentStatus,
        IDataProtectionProvider protection)
    {
        var canVoluntaryCancel = row.Status is "AWAITING_PAYMENT" or "PAID_REVIEW";
        return new KioskApplicationResponse(
            row.Id,
            row.CustomerId,
            row.PackageId,
            row.PackageName ?? "Kiosk theo nhu cầu",
            row.AnnualFee.GetValueOrDefault(10000),
            row.TotalSlots.GetValueOrDefault(2),
            row.ShopName,
            row.Phone,
            row.ContactEmail,
            row.InstallationAddress,
            row.Latitude,
            row.Longitude,
            row.BankName,
            row.BankHolder,
            MaskAccount(row.BankAccountNoEnc, protection),
            row.Status,
            row.CreatedAt,
            row.UpdatedAt,
            row.PaymentId,
            paymentStatus,
            row.ReviewNote,
            row.ConfirmedAt,
            row.AssignedTechnicianId,
            technicianName,
            row.AssignedKioskId,
            kioskCode,
            row.ScheduledDate,
            row.TimeWindow,
            row.TechnicianReport,
            row.ReworkReason,
            row.HandoverAt,
            row.HandoverBy,
            row.SellerId,
            canVoluntaryCancel,
            row.WalkInSlots, row.PreorderSlots, row.PricingMode,
            row.WalkInAnnualPrice, row.PreorderAnnualPrice, row.PricingVersion
        );
    }

    private static bool ValidSlotDemand(int? walk, int? pre) =>
        walk is >= 0 and <= 50 && pre is >= 0 and <= 50 && walk.Value + pre.Value is >= 1 and <= 50;

    private static async Task<(string? TechName, string? KioskCode)> ResolveNames(
        NpgsqlDataSource data,
        Guid? techId,
        Guid? kioskId,
        CancellationToken ct)
    {
        string? techName = null;
        if (techId.HasValue)
        {
            var dict = await StaffIdentityRead.Users(data, [techId.Value], ct);
            techName = dict.GetValueOrDefault(techId.Value);
        }

        string? kioskCode = null;
        if (kioskId.HasValue)
        {
            var dict = await ResolveKioskCodes(data, [kioskId.Value], ct);
            kioskCode = dict.GetValueOrDefault(kioskId.Value);
        }

        return (techName, kioskCode);
    }

    private static async Task<Dictionary<Guid, string>> ResolveKioskCodes(NpgsqlDataSource data, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return new();
        await using var command = data.CreateCommand("SELECT id, code FROM kiosk_ops.kiosks WHERE id = ANY(@ids)");
        command.Parameters.AddWithValue("ids", ids);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<Guid, string>();
        while (await reader.ReadAsync(ct))
            result[reader.GetGuid(0)] = reader.GetString(1);
        return result;
    }

    private static async Task<string?> ResolvePaymentStatus(NpgsqlDataSource data, Guid paymentId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT flow.get_payment_status(@id)");
        command.Parameters.AddWithValue("id", paymentId);
        var obj = await command.ExecuteScalarAsync(ct);
        return obj as string;
    }

    private static async Task<List<IncidentPhoto>> ReadEvidencePhotos(NpgsqlDataSource data, Guid appId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT id, created_at FROM flow.list_kiosk_evidence(@id)");
        command.Parameters.AddWithValue("id", appId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<IncidentPhoto>();
        while (await reader.ReadAsync(ct))
            list.Add(new IncidentPhoto(reader.GetGuid(0), $"/api/operations/kiosk-applications/{appId}/evidence/{reader.GetGuid(0)}", reader.GetDateTime(1)));
        return list;
    }

    private static async Task<List<StaffEvidenceItem>> ReadEvidenceItems(NpgsqlDataSource data, Guid appId, CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT id, created_at FROM flow.list_kiosk_evidence(@id)");
        command.Parameters.AddWithValue("id", appId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<StaffEvidenceItem>();
        while (await reader.ReadAsync(ct))
            list.Add(new StaffEvidenceItem(reader.GetGuid(0), reader.GetDateTime(1)));
        return list;
    }

    private static async Task<IResult> ServeAttachmentFile(
        NpgsqlDataSource data,
        CloudinaryMedia media,
        Guid appId,
        Guid attachmentId,
        string phase,
        CancellationToken ct)
    {
        await using var command = data.CreateCommand("SELECT file_url, mime_type, size_bytes, sha256 FROM flow.get_kiosk_attachment(@attId, @appId, @phase)");
        command.Parameters.AddWithValue("attId", attachmentId);
        command.Parameters.AddWithValue("appId", appId);
        command.Parameters.AddWithValue("phase", phase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return Results.NotFound();

        var url = reader.GetString(0);
        var mime = reader.GetString(1);
        var size = reader.GetInt64(2);
        var hash = reader.GetString(3);
        await reader.DisposeAsync();

        try
        {
            var bytes = await media.ReadPrivateAsync(url, ct);
            if (bytes.Length != size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash || CloudinaryMedia.ImageType(bytes) != mime)
                return Results.Problem(statusCode: 503, detail: "Tệp đính kèm không nguyên vẹn.");
            return Results.File(bytes, mime, enableRangeProcessing: false);
        }
        catch (Exception ex) when (ex is MediaUnavailableException or HttpRequestException or JsonException or ArgumentException)
        {
            return Results.Problem(statusCode: 503, detail: "Không thể tải tệp bằng chứng lúc này.");
        }
    }
}
