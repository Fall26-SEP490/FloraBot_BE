using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Notify;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FloraBot.Api.Modules.Identity;

public sealed record PersonalBankInfo(
    string BankName,
    string AccountHolder,
    string AccountLast4,
    bool HasAccount,
    string? AccountNumber = null,
    string? Status = null);

public sealed record UserProfileResponse(
    Guid Id,
    string FullName,
    string? LoginEmail,
    string? Phone,
    string? Address,
    string? ContactEmail,
    bool ContactEmailIsDefault,
    bool ContactEmailVerified,
    PersonalBankInfo? Bank,
    string? Gender = null,
    DateOnly? DateOfBirth = null);

public sealed record PersonalBankInput(
    string BankName,
    string AccountHolder,
    string AccountNumber);

public sealed record UpdateUserProfileRequest(
    string FullName,
    string? Phone,
    string? Address,
    PersonalBankInput? Bank = null,
    bool? ClearBank = null,
    string? Gender = null,
    DateOnly? DateOfBirth = null,
    bool? ClearGender = null,
    bool? ClearDateOfBirth = null);

public sealed record RevealBankResponse(string AccountNumber);

public sealed record RequestContactEmailInput(string Email);

public sealed record ContactEmailChallengeResponse(
    string ChallengeId,
    int ExpiresInSeconds,
    int ResendAfterSeconds,
    string Message);

public sealed record VerifyContactEmailInput(string ChallengeId, string Code);

public static partial class PersonalProfileEndpoints
{
    private const string DataProtectionPurpose = "personal-bank-account-v1";

    private static readonly HashSet<string> AllowedGenders = new(StringComparer.OrdinalIgnoreCase)
    {
        "MALE", "FEMALE", "OTHER", "UNDISCLOSED"
    };

    private static DateOnly GetVietnamToday() =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(7));

    [GeneratedRegex("^(0[35789][0-9]{8}|\\+84[35789][0-9]{8})$")]
    private static partial Regex PhonePattern();

    public static void MapPersonalProfile(this WebApplication app)
    {
        app.MapGet("/api/account/profile", async (FloraDbContext db, IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actorId = GetActorId(http.User);
            if (actorId is null) return Results.Unauthorized();

            var user = await db.Users.AsNoTracking()
                .Include(u => u.Profile)
                .SingleOrDefaultAsync(u => u.Id == actorId.Value && u.Status == "ACTIVE", ct);
            if (user is null) return Results.Unauthorized();

            string? decryptedAccount = null;
            string? bankStatus = null;
            var profile = user.Profile;

            if (profile is not null &&
                !string.IsNullOrWhiteSpace(profile.BankName) &&
                !string.IsNullOrWhiteSpace(profile.AccountHolder) &&
                !string.IsNullOrWhiteSpace(profile.AccountEncrypted) &&
                !string.IsNullOrWhiteSpace(profile.AccountLast4))
            {
                try
                {
                    var raw = protection.CreateProtector(DataProtectionPurpose).Unprotect(profile.AccountEncrypted);
                    if (!SellerBankInput.ValidAccount(raw))
                    {
                        bankStatus = "NEEDS_UPDATE";
                    }
                    else
                    {
                        decryptedAccount = raw;
                        bankStatus = "READY";
                    }
                }
                catch (CryptographicException)
                {
                    bankStatus = "NEEDS_UPDATE";
                }

                if (decryptedAccount is not null)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT flow.audit({actorId.Value}, 'PERSONAL_BANK_REVEALED', 'user_profiles', {actorId.Value})", ct);
                }
            }

            return Results.Ok(ToProfileResponse(user, profile, decryptedAccount, bankStatus));
        })
        .RequireAuthorization("Account")
        .WithTags("Account")
        .WithSummary("Lấy thông tin hồ sơ cá nhân của tài khoản đang đăng nhập")
        .Produces<UserProfileResponse>()
        .ProducesProblem(401).ProducesProblem(403);

        app.MapPut("/api/account/profile", async (UpdateUserProfileRequest input, FloraDbContext db, IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actorId = GetActorId(http.User);
            if (actorId is null) return Results.Unauthorized();

            var errors = new Dictionary<string, string[]>();

            var name = input.FullName?.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length is < 2 or > 100 || name.Any(char.IsControl))
                errors["fullName"] = ["Họ và tên cần từ 2 đến 100 ký tự và không chứa ký tự điều khiển."];

            string? address = null;
            if (input.Address is not null)
            {
                var trimmedAddress = input.Address.Trim();
                if (trimmedAddress.Length > 300 || trimmedAddress.Any(char.IsControl))
                    errors["address"] = ["Địa chỉ tối đa 300 ký tự và không chứa ký tự điều khiển."];
                else if (!string.IsNullOrWhiteSpace(trimmedAddress))
                    address = trimmedAddress;
            }

            string? normalizedPhone = null;
            if (input.Phone is not null)
            {
                var trimmedPhone = input.Phone.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedPhone))
                {
                    if (!PhonePattern().IsMatch(trimmedPhone))
                        errors["phone"] = ["Số điện thoại Việt Nam không hợp lệ."];
                    else
                        normalizedPhone = trimmedPhone.StartsWith("+84", StringComparison.Ordinal) ? "0" + trimmedPhone[3..] : trimmedPhone;
                }
            }

            if (input.Bank is not null && input.ClearBank == true)
            {
                errors["bank"] = ["Không thể đồng thời cập nhật và xóa thông tin tài khoản ngân hàng."];
            }
            else if (input.Bank is not null)
            {
                var bankName = input.Bank.BankName?.Trim();
                var holder = input.Bank.AccountHolder?.Trim();
                var account = input.Bank.AccountNumber?.Trim();

                if (string.IsNullOrWhiteSpace(bankName) || bankName.Length is < 1 or > 120 || bankName.Any(char.IsControl))
                    errors["bank.bankName"] = ["Tên ngân hàng cần từ 1 đến 120 ký tự và không chứa ký tự điều khiển."];
                if (string.IsNullOrWhiteSpace(holder) || holder.Length is < 1 or > 120 || holder.Any(char.IsControl))
                    errors["bank.accountHolder"] = ["Tên chủ tài khoản cần từ 1 đến 120 ký tự và không chứa ký tự điều khiển."];
                if (string.IsNullOrWhiteSpace(account) || !SellerBankInput.ValidAccount(account))
                    errors["bank.accountNumber"] = ["Số tài khoản gồm 4–34 chữ cái hoặc chữ số, không có khoảng trắng."];
            }

            if (input.ClearGender == true && input.Gender is not null)
            {
                errors["gender"] = ["Không thể đồng thời cập nhật và xóa thông tin giới tính."];
            }
            else if (input.ClearGender != true && input.Gender is not null)
            {
                var trimmedGender = input.Gender.Trim();
                if (!AllowedGenders.Contains(trimmedGender))
                {
                    errors["gender"] = ["Giới tính không hợp lệ. Cho phép: MALE, FEMALE, OTHER, UNDISCLOSED."];
                }
            }

            if (input.ClearDateOfBirth == true && input.DateOfBirth.HasValue)
            {
                errors["dateOfBirth"] = ["Không thể đồng thời cập nhật và xóa thông tin ngày sinh."];
            }
            else if (input.ClearDateOfBirth != true && input.DateOfBirth.HasValue)
            {
                var todayVn = GetVietnamToday();
                if (input.DateOfBirth.Value > todayVn)
                {
                    errors["dateOfBirth"] = ["Ngày sinh không được vượt quá ngày hiện tại theo giờ Việt Nam."];
                }
            }

            if (errors.Count > 0)
                return Results.ValidationProblem(errors);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Serialize profile mutation per user using short DB row lock
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {actorId.Value} FOR UPDATE", ct);

            if (normalizedPhone is not null)
            {
                var phoneTaken = await db.Users.AnyAsync(u => u.Phone == normalizedPhone && u.Id != actorId.Value, ct);
                if (phoneTaken)
                {
                    await transaction.RollbackAsync(ct);
                    return Results.Problem(statusCode: 409, detail: "Số điện thoại đã được sử dụng bởi một tài khoản khác.");
                }
            }

            var user = await db.Users.Include(u => u.Profile).SingleOrDefaultAsync(u => u.Id == actorId.Value && u.Status == "ACTIVE", ct);
            if (user is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            var changedFields = new List<string>();
            if (user.FullName != name) changedFields.Add("full_name");
            if (user.Phone != normalizedPhone) changedFields.Add("phone");

            user.FullName = name!;
            user.Phone = normalizedPhone;
            user.UpdatedAt = DateTime.UtcNow;

            var profile = user.Profile;
            if (profile is null)
            {
                profile = new UserProfile
                {
                    UserId = user.Id,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.UserProfiles.Add(profile);
            }
            else
            {
                profile.UpdatedAt = DateTime.UtcNow;
            }

            if (profile.Address != address) changedFields.Add("address");
            profile.Address = address;

            if (input.ClearGender == true)
            {
                if (profile.Gender is not null) changedFields.Add("gender_cleared");
                profile.Gender = null;
            }
            else if (input.Gender is not null)
            {
                var normalizedGender = input.Gender.Trim().ToUpperInvariant();
                if (profile.Gender != normalizedGender) changedFields.Add("gender");
                profile.Gender = normalizedGender;
            }

            if (input.ClearDateOfBirth == true)
            {
                if (profile.DateOfBirth.HasValue) changedFields.Add("date_of_birth_cleared");
                profile.DateOfBirth = null;
            }
            else if (input.DateOfBirth.HasValue)
            {
                if (profile.DateOfBirth != input.DateOfBirth.Value) changedFields.Add("date_of_birth");
                profile.DateOfBirth = input.DateOfBirth.Value;
            }

            if (input.ClearBank == true)
            {
                if (profile.BankName is not null || profile.AccountEncrypted is not null) changedFields.Add("bank_cleared");
                profile.BankName = null;
                profile.AccountHolder = null;
                profile.AccountEncrypted = null;
                profile.AccountLast4 = null;
            }
            else if (input.Bank is not null)
            {
                changedFields.Add("bank_updated");
                var bankName = input.Bank.BankName!.Trim();
                var holder = input.Bank.AccountHolder!.Trim().ToUpperInvariant();
                var account = input.Bank.AccountNumber!.Trim();

                profile.BankName = bankName;
                profile.AccountHolder = holder;
                profile.AccountEncrypted = protection.CreateProtector(DataProtectionPurpose).Protect(account);
                profile.AccountLast4 = account.Length > 4 ? account[^4..] : account;
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pe && pe.SqlState == "23505")
            {
                await transaction.RollbackAsync(ct);
                return Results.Problem(statusCode: 409, detail: "Số điện thoại đã được sử dụng bởi một tài khoản khác.");
            }

            if (changedFields.Count > 0)
            {
                var auditPayload = JsonSerializer.Serialize(new { fields = changedFields });
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT flow.audit({actorId.Value}, 'PERSONAL_PROFILE_UPDATED', 'user_profiles', {actorId.Value}, {auditPayload}::jsonb)", ct);
            }

            await transaction.CommitAsync(ct);

            string? returnedBankNumber = null;
            string? returnedBankStatus = null;
            if (input.Bank is not null)
            {
                returnedBankNumber = input.Bank.AccountNumber?.Trim();
                returnedBankStatus = "READY";
            }

            return Results.Ok(ToProfileResponse(user, profile, returnedBankNumber, returnedBankStatus));
        })
        .RequireAuthorization("Account")
        .WithTags("Account")
        .WithSummary("Cập nhật thông tin hồ sơ cá nhân và tài khoản ngân hàng")
        .Produces<UserProfileResponse>()
        .ProducesValidationProblem()
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);

        app.MapPost("/api/account/profile/bank/reveal", async (FloraDbContext db, IDataProtectionProvider protection, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actorId = GetActorId(http.User);
            if (actorId is null) return Results.Unauthorized();

            var profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == actorId.Value, ct);
            if (profile is null || string.IsNullOrWhiteSpace(profile.AccountEncrypted))
                return Results.NotFound();

            string rawAccount;
            try
            {
                rawAccount = protection.CreateProtector(DataProtectionPurpose).Unprotect(profile.AccountEncrypted);
            }
            catch (CryptographicException)
            {
                return Results.NotFound();
            }

            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT flow.audit({actorId.Value}, 'PERSONAL_BANK_REVEALED', 'user_profiles', {actorId.Value})", ct);

            return Results.Ok(new RevealBankResponse(rawAccount));
        })
        .RequireAuthorization("Account")
        .WithTags("Account")
        .WithSummary("Hiển thị đầy đủ số tài khoản ngân hàng cá nhân với ghi log kiểm toán")
        .Produces<RevealBankResponse>()
        .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        app.MapPost("/api/account/contact-email", async (RequestContactEmailInput input, FloraDbContext db, ContactEmailOtpService contactOtp, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actorId = GetActorId(http.User);
            if (actorId is null) return Results.Unauthorized();

            var email = input.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email) || !EmailSettings.IsAddress(email) || email.Length > 255)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["Nhập địa chỉ email hợp lệ."] });

            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == actorId.Value && u.Status == "ACTIVE", ct);
            if (user is null) return Results.Unauthorized();

            var startResult = await contactOtp.StartContactEmailAsync(actorId.Value, email, user.FullName, ct);
            return startResult switch
            {
                ContactEmailStartResult.Success s =>
                    Results.Ok(new ContactEmailChallengeResponse(s.ChallengeId, s.ExpiresInSeconds, s.ResendAfterSeconds, s.Message)),
                ContactEmailStartResult.Cooldown =>
                    Results.Problem(statusCode: 429, detail: "Vui lòng đợi 60 giây trước khi yêu cầu mã xác thực mới."),
                ContactEmailStartResult.Locked =>
                    Results.Problem(statusCode: 429, detail: "Bạn đã thử sai quá số lần cho phép. Vui lòng thử lại sau 15 phút."),
                ContactEmailStartResult.EmailUnavailable =>
                    Results.Problem(statusCode: 503, detail: "Dịch vụ email hiện chưa sẵn sàng. Vui lòng liên hệ đội ngũ hỗ trợ."),
                ContactEmailStartResult.Superseded =>
                    Results.Problem(statusCode: 409, detail: "Yêu cầu đổi email liên hệ đã bị hủy hoặc thay thế."),
                _ => Results.Problem(statusCode: 500, detail: "Không thể xử lý yêu cầu xác thực email.")
            };
        })
        .RequireAuthorization("Account")
        .RequireRateLimiting("contact-email")
        .WithTags("Account")
        .WithSummary("Yêu cầu gửi mã OTP để đổi email liên hệ")
        .Produces<ContactEmailChallengeResponse>()
        .ProducesValidationProblem()
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        app.MapPost("/api/account/contact-email/verify", async (VerifyContactEmailInput input, FloraDbContext db, ContactEmailOtpService contactOtp, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actorId = GetActorId(http.User);
            if (actorId is null) return Results.Unauthorized();

            // Hold the same user lock as reset BEFORE consuming the OTP. Otherwise a
            // consumed verification could commit after a later reset has completed.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {actorId.Value} FOR UPDATE", ct);
            var verifyResult = await contactOtp.VerifyContactEmailAsync(actorId.Value, input.ChallengeId, input.Code, ct);
            if (verifyResult is ContactEmailVerifyResult.Success success)
            {
                var response = await UpdateVerifiedContactEmail(actorId.Value, success.VerifiedEmail, db, ct);
                await transaction.CommitAsync(ct);
                return response;
            }
            return verifyResult switch
            {
                ContactEmailVerifyResult.WrongCode w =>
                    Results.Problem(statusCode: 400, detail: $"Mã xác thực không chính xác. Bạn còn {Math.Max(0, 5 - w.Attempts)} lần thử."),
                ContactEmailVerifyResult.Locked =>
                    Results.Problem(statusCode: 429, detail: "Bạn đã thử sai quá số lần cho phép. Vui lòng thử lại sau 15 phút."),
                ContactEmailVerifyResult.InvalidOrExpired =>
                    Results.Problem(statusCode: 400, detail: "Mã xác thực không hợp lệ hoặc đã hết hạn."),
                _ => Results.Problem(statusCode: 400, detail: "Xác thực không thành công.")
            };
        })
        .RequireAuthorization("Account")
        .RequireRateLimiting("contact-email")
        .WithTags("Account")
        .WithSummary("Xác thực mã OTP và cập nhật email liên hệ mới")
        .Produces<UserProfileResponse>()
        .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);

        app.MapPost("/api/account/contact-email/reset", async (FloraDbContext db, ContactEmailOtpService contactOtp, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var actorId = GetActorId(http.User);
            if (actorId is null) return Results.Unauthorized();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM identity.users WHERE id = {actorId.Value} FOR UPDATE", ct);
            await contactOtp.ResetContactEmailAsync(actorId.Value, ct);

            var user = await db.Users.Include(u => u.Profile).SingleOrDefaultAsync(u => u.Id == actorId.Value && u.Status == "ACTIVE", ct);
            if (user is null)
            {
                await transaction.RollbackAsync(ct);
                return Results.Unauthorized();
            }

            if (user.Profile is not null)
            {
                user.Profile.ContactEmail = null;
                user.Profile.ContactEmailVerifiedAt = null;
                user.Profile.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            var auditPayload = JsonSerializer.Serialize(new { action = "contact_email_reset" });
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT flow.audit({actorId.Value}, 'CONTACT_EMAIL_RESET', 'user_profiles', {actorId.Value}, {auditPayload}::jsonb)", ct);

            await transaction.CommitAsync(ct);

            return Results.Ok(ToProfileResponse(user, user.Profile));
        })
        .RequireAuthorization("Account")
        .WithTags("Account")
        .WithSummary("Đặt lại email liên hệ về email đăng nhập mặc định")
        .Produces<UserProfileResponse>()
        .ProducesProblem(401).ProducesProblem(403);
    }

    private static async Task<IResult> UpdateVerifiedContactEmail(Guid userId, string verifiedEmail, FloraDbContext db, CancellationToken ct)
    {
        var user = await db.Users.Include(u => u.Profile).SingleOrDefaultAsync(u => u.Id == userId && u.Status == "ACTIVE", ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var profile = user.Profile;
        if (profile is null)
        {
            profile = new UserProfile
            {
                UserId = user.Id,
                ContactEmail = verifiedEmail,
                ContactEmailVerifiedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserProfiles.Add(profile);
        }
        else
        {
            profile.ContactEmail = verifiedEmail;
            profile.ContactEmailVerifiedAt = DateTime.UtcNow;
            profile.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        var auditPayload = JsonSerializer.Serialize(new { action = "contact_email_verified" });
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT flow.audit({userId}, 'CONTACT_EMAIL_VERIFIED', 'user_profiles', {userId}, {auditPayload}::jsonb)", ct);

        return Results.Ok(ToProfileResponse(user, profile));
    }

    private static UserProfileResponse ToProfileResponse(
        User user,
        UserProfile? profile,
        string? decryptedBankNumber = null,
        string? bankStatus = null)
    {
        var hasVerifiedCustomContact = profile?.ContactEmailVerifiedAt is not null && !string.IsNullOrWhiteSpace(profile?.ContactEmail);
        var contactEmail = hasVerifiedCustomContact ? profile!.ContactEmail : user.Email;
        var contactEmailIsDefault = !hasVerifiedCustomContact;
        var contactEmailVerified = hasVerifiedCustomContact;

        PersonalBankInfo? bank = null;
        if (profile is not null &&
            !string.IsNullOrWhiteSpace(profile.BankName) &&
            !string.IsNullOrWhiteSpace(profile.AccountHolder) &&
            !string.IsNullOrWhiteSpace(profile.AccountEncrypted) &&
            !string.IsNullOrWhiteSpace(profile.AccountLast4))
        {
            bank = new PersonalBankInfo(
                profile.BankName,
                profile.AccountHolder,
                profile.AccountLast4,
                true,
                decryptedBankNumber,
                bankStatus ?? (decryptedBankNumber is not null ? "READY" : null));
        }

        return new UserProfileResponse(
            user.Id,
            user.FullName,
            user.Email,
            user.Phone,
            profile?.Address,
            contactEmail,
            contactEmailIsDefault,
            contactEmailVerified,
            bank,
            profile?.Gender,
            profile?.DateOfBirth);
    }

    private static Guid? GetActorId(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
