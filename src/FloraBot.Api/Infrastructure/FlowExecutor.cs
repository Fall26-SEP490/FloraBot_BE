using System.Data;
using System.Security.Claims;
using System.Text.Json;
using FloraBot.Api.Realtime;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;
using DotNetCore.CAP;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Payment;
using FloraBot.Api.Modules.Notify;

namespace FloraBot.Api.Infrastructure;

public sealed record FlowParameter(string Name, string Type, bool Optional);
public sealed record FlowDefinition(string Name, string Module, string Scope, FlowParameter[] Params, bool SetReturning);
public sealed record FlowResult(JsonElement? Result);

public sealed class FlowExecutor(NpgsqlDataSource dataSource, IDataProtectionProvider protection, PortalNotifier notifier, ICapPublisher publisher, CloudinaryMedia media, PrivateEvidence evidence)
{
    public static readonly IReadOnlyDictionary<string, FlowDefinition> Definitions =
        JsonSerializer.Deserialize<FlowDefinition[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Infrastructure", "flows.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.ToDictionary(x => x.Name);

    public async Task<IResult> ExecuteAsync(string name, string scope, JsonElement input, HttpContext http, CancellationToken ct)
    {
        var user = http.User;
        var actor = user.FindFirstValue("sub");
        var sellerRoute = http.Request.RouteValues["sellerId"]?.ToString();
        var kioskRoute = http.Request.RouteValues["kioskId"]?.ToString();
        if (scope != "receipt" && user.IsInRole("SELLER") && sellerRoute != user.FindFirstValue("seller_id")) return Results.NotFound();
        if (kioskRoute is not null && kioskRoute != user.FindFirstValue("kiosk_id")) return Results.Forbid();
        var args = input.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToDictionary(p => p.Name, p => p.Value.Clone());
        foreach (var parameter in Definitions[name].Params)
        {
            if (parameter.Name is "p_user" or "p_admin" or "p_actor" or "p_staff" or "p_reporter" or "p_approver")
                args[parameter.Name] = JsonSerializer.SerializeToElement(actor);
            if (parameter.Name == "p_seller" && sellerRoute is not null) args[parameter.Name] = JsonSerializer.SerializeToElement(sellerRoute);
            if (parameter.Name == "p_kiosk" && kioskRoute is not null) args[parameter.Name] = JsonSerializer.SerializeToElement(kioskRoute);
            if (parameter.Name == "p_customer") args[parameter.Name] = JsonSerializer.SerializeToElement(user.IsInRole("CUSTOMER") ? actor : null);
        }
        if (name is "reconcile_gateway" or "reconcile_daily")
        {
            var invalid = ReconciliationInput.Normalize(name, args);
            if (invalid is not null) return invalid;
        }
        if (name == "kiosk_checkout" && args.TryGetValue("p_points", out var points) && points.GetInt64() < 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["p_points"] = ["Số điểm sử dụng phải lớn hơn hoặc bằng 0."]
            });
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        using var outbox = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, publisher, cancellationToken: ct);
        var transaction = (NpgsqlTransaction)outbox.DbTransaction!;
        if (name is "reconcile_gateway" or "reconcile_daily")
        {
            await using var timezone = new NpgsqlCommand("SET LOCAL TIME ZONE 'Asia/Ho_Chi_Minh'", connection, transaction);
            await timezone.ExecuteNonQueryAsync(ct);
        }
        if (scope == "receipt")
        {
            var invalidReceipt = await Modules.Ordering.ReceiptAccess.CheckAsync(connection, transaction, args, ct);
            if (invalidReceipt is not null) return invalidReceipt;
        }
        // Authorization and flow run under one transaction; selected resources are locked against reassignment.
        var denied = await ResourceAccess.CheckAsync(connection, transaction, user, name, args, ct);
        if (denied is not null) return denied;
        VerifiedMedia? verifiedEvidence = null;
        if (name is "return_to_seller" or "report_device_fault" or "open_dispute" or "confirm_refund" or "pay_withdrawal")
        {
            var financial = name is "confirm_refund" or "pay_withdrawal";
            var field = financial ? "p_proof_url" : name == "return_to_seller" ? "p_photo_url" : "p_photo";
            if (!args.TryGetValue(field, out var reference) || reference.ValueKind != JsonValueKind.String)
                return Results.Problem(statusCode: 400, detail: "Vui lòng tải ảnh minh chứng trước khi gửi.");
            var binding = financial ? PrivateEvidence.AdminBinding(actor!, args[name == "confirm_refund" ? "p_refund" : "p_w"].GetGuid()) : name == "open_dispute"
                ? PrivateEvidence.ReceiptBinding(args["p_order"].GetGuid(), args["p_tracking"].GetString()!)
                : PrivateEvidence.SellerBinding(actor!, Guid.Parse(sellerRoute!), args["p_slot"].GetGuid());
            try { verifiedEvidence = evidence.Resolve(reference.GetString()!, name, binding); }
            catch (ArgumentException ex) { return Results.Problem(statusCode: 400, detail: ex.Message); }
            if (!await PrivateEvidence.LockUnusedAsync(connection, transaction, verifiedEvidence.Url, ct))
                return Results.Problem(statusCode: 409, detail: "Ảnh này đã được dùng. Kiểm tra kết quả lần gửi trước trước khi tạo yêu cầu mới.");
            args[field] = JsonSerializer.SerializeToElement(verifiedEvidence.Url);
        }
        VerifiedMedia? verifiedPhoto = null;
        if (name == "add_product_photo")
        {
            if (!args.TryGetValue("p_url", out var photoUrl) || photoUrl.ValueKind != JsonValueKind.String)
                return Results.Problem(statusCode: 400, detail: "Cần URL ảnh gốc trong kho FloraBot đã cấu hình.");
            try { verifiedPhoto = await media.VerifyUrlAsync(photoUrl.GetString(), ct); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, detail: "URL hoặc nội dung ảnh không hợp lệ. Chọn ảnh PNG, JPEG hoặc WebP trong kho FloraBot."); }
            catch (Exception ex) when (ex is MediaUnavailableException or HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                return Results.Problem(statusCode: 503, detail: "Chưa xác minh được ảnh đã lưu. Vui lòng thử lại sau.");
            }
            args["p_url"] = JsonSerializer.SerializeToElement(verifiedPhoto.Url);
        }
        if (name == "set_seller_bank")
        {
            var invalid = Modules.Identity.SellerBankInput.Normalize(args);
            if (invalid is not null) return invalid;
        }
        if (name == "submit_refund_info")
        {
            var invalid = Modules.Ordering.RefundBankInput.Normalize(args);
            if (invalid is not null) return invalid;
        }
        if (name == "open_dispute")
        {
            var invalid = Modules.Ordering.ReceiptComplaintInput.Normalize(args);
            if (invalid is not null) return invalid;
        }
        if (args.TryGetValue("p_account_enc", out var account))
            args["p_account_enc"] = JsonSerializer.SerializeToElement(protection.CreateProtector("bank-account-v1").Protect(account.GetString()!));
        var device = name is "device_event" or "admin_close_door"
            ? await DeviceEvents.BeforeAsync(connection, transaction, args["p_cmd"].GetGuid(), ct) : null;
        var watermark = await Modules.Notify.TransactionChanges.WatermarkAsync(connection, transaction, ct);
        var result = await CallAsync(connection, transaction, name, args, ct);
        if (verifiedEvidence is not null)
            await PrivateEvidence.SetFlowMetadataAsync(connection, transaction, verifiedEvidence, ct);
        if (verifiedPhoto is not null)
            await VerifiedAttachments.SetMetadataAsync(connection, transaction, result!.Value.GetGuid(), verifiedPhoto, ct);
        if (name == "reconcile_daily")
            await Modules.Notify.ReconciliationAudit.RecordDailyAsync(connection, transaction, Guid.Parse(actor!), args["p_date"], result, ct);
        if (device is not null) await DeviceEvents.AfterAsync(connection, transaction, publisher, device, ct);
        await DomainChanges.PublishAsync(connection, transaction, publisher, watermark, ct);
        if ((name is "admin_refund" or "resolve_dispute") && result is { ValueKind: JsonValueKind.String } refund && refund.TryGetGuid(out var refundId))
            await RefundEvents.PublishByIdAsync(connection, transaction, publisher, refundId, ct);
        await outbox.CommitAsync(ct);
        // Return the connection before the notifier performs its independent identity lookup.
        await connection.CloseAsync();
        if (name != "ai_suggest")
        {
            Guid? targetSeller = Guid.TryParse(sellerRoute, out var routeId) ? routeId :
                args.TryGetValue("p_seller", out var sellerValue) && sellerValue.ValueKind == JsonValueKind.String && sellerValue.TryGetGuid(out var sellerId) ? sellerId : null;
            await notifier.RefreshAsync(targetSeller);
        }
        return Results.Ok(new FlowResult(result));
    }

    public static async Task<JsonElement?> CallAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string name,
        IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct)
    {
        var definition = Definitions[name];
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = new List<string>();
        foreach (var p in definition.Params)
        {
            if (p.Name == "p_now") continue;
            if (!args.TryGetValue(p.Name, out var value))
            {
                if (p.Optional) continue;
                throw new BadHttpRequestException($"Thiếu tham số {p.Name}.");
            }
            names.Add($"{p.Name} => @{p.Name}");
            var (dbType, converted) = ConvertParameter(p.Type, value);
            command.Parameters.AddWithValue(p.Name, dbType, converted ?? DBNull.Value);
        }
        var resultExpression = definition.SetReturning ? "coalesce(jsonb_agg(to_jsonb(result)), '[]'::jsonb)" : "to_jsonb(result)";
        command.CommandText = $"SELECT {resultExpression} FROM flow.{definition.Name}({string.Join(",", names)}) AS result";
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : JsonSerializer.Deserialize<JsonElement>(result.ToString()!);
    }

    private static (NpgsqlDbType, object?) ConvertParameter(string type, JsonElement value)
    {
        var dbType = type switch
        {
            "uuid" => NpgsqlDbType.Uuid,
            "uuid[]" => NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            "text" => NpgsqlDbType.Text,
            "int" => NpgsqlDbType.Integer,
            "bigint" => NpgsqlDbType.Bigint,
            "numeric" => NpgsqlDbType.Numeric,
            "boolean" => NpgsqlDbType.Boolean,
            "date" => NpgsqlDbType.Date,
            "jsonb" => NpgsqlDbType.Jsonb,
            _ => throw new InvalidOperationException(type)
        };
        if (value.ValueKind == JsonValueKind.Null) return (dbType, null);
        object? converted = type switch
        {
            "uuid" => value.GetGuid(),
            "uuid[]" => value.EnumerateArray().Select(x => x.GetGuid()).ToArray(),
            "text" => value.GetString(),
            "int" => value.GetInt32(),
            "bigint" => value.GetInt64(),
            "numeric" => value.GetDecimal(),
            "boolean" => value.GetBoolean(),
            "date" => DateOnly.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            "jsonb" => value.GetRawText(),
            _ => null
        };
        return (dbType, converted);
    }
}
