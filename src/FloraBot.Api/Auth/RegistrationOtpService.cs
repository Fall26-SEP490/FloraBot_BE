using System.Security.Cryptography;
using System.Text;
using FloraBot.Api.Modules.Notify;
using StackExchange.Redis;

namespace FloraBot.Api.Auth;

public abstract record RegistrationStartResult
{
    public sealed record Success(string ChallengeId, int ExpiresInSeconds, int ResendAfterSeconds, string Message) : RegistrationStartResult;
    public sealed record Cooldown(int RetryAfterSeconds) : RegistrationStartResult;
    public sealed record Locked() : RegistrationStartResult;
    public sealed record EmailUnavailable() : RegistrationStartResult;
}

public abstract record RegistrationVerifyResult
{
    public sealed record Success(string Email, string FullName, string PasswordHash) : RegistrationVerifyResult;
    public sealed record InvalidOrExpired() : RegistrationVerifyResult;
    public sealed record WrongCode(int Attempts) : RegistrationVerifyResult;
    public sealed record Locked() : RegistrationVerifyResult;
}

public sealed class RegistrationOtpService(
    IConnectionMultiplexer redis,
    IConfiguration configuration,
    ITransactionalEmailSender emailSender,
    EmailSettings emailSettings)
{
    private static bool IsHex32(string s) =>
        s.Length == 32 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static bool IsAsciiDigits6(string s) =>
        s.Length == 6 && s.All(c => c is >= '0' and <= '9');

    private string EmailHash(string email) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));

    private string Hmac(string challengeId, string email, string code)
    {
        var key = Encoding.UTF8.GetBytes(configuration["JWT_SIGNING_KEY"]!);
        var message = Encoding.UTF8.GetBytes($"reg-otp:{challengeId.Trim().ToLowerInvariant()}:{email.Trim().ToLowerInvariant()}:{code.Trim()}");
        return Convert.ToHexString(HMACSHA256.HashData(key, message));
    }

    public async Task<RegistrationStartResult> StartRegistrationAsync(string fullName, string email, string password, CancellationToken ct)
    {
        if (emailSettings.Provider == EmailProvider.Disabled)
            return new RegistrationStartResult.EmailUnavailable();

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var emailHash = EmailHash(normalizedEmail);
        var db = redis.GetDatabase();

        var lockKey = $"reg:email:{emailHash}:lock";
        var cooldownKey = $"reg:email:{emailHash}:cooldown";
        var leaseKey = $"reg:email:{emailHash}:lease";

        // Atomic Valkey reservation before provider call: simultaneous starts produce only ONE provider send.
        const string reserveScript = """
            if redis.call('EXISTS', KEYS[1]) == 1 then
                return -1
            end
            if redis.call('EXISTS', KEYS[2]) == 1 or redis.call('EXISTS', KEYS[3]) == 1 then
                return -2
            end
            redis.call('SET', KEYS[3], '1', 'EX', 60)
            return 1
            """;

        var reserveResult = (int)await db.ScriptEvaluateAsync(reserveScript, [lockKey, cooldownKey, leaseKey]);
        if (reserveResult == -1)
            return new RegistrationStartResult.Locked();
        if (reserveResult == -2)
            return new RegistrationStartResult.Cooldown(60);

        var challengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
        var subject = "Mã xác thực đăng ký tài khoản FloraBot";
        var text = $"Mã xác thực của bạn là: {code}\nMã có hiệu lực trong 10 phút. Không chia sẻ mã này với bất kỳ ai.";

        try
        {
            await emailSender.SendAsync(new TransactionalEmail(normalizedEmail, subject, text), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Bounded failure: avoid logging passwords, tokens, challenge contents, or provider exceptions.
            // Retain lease as cooldown to prevent provider hammering without creating accounts or fake success.
            await db.KeyExpireAsync(leaseKey, TimeSpan.FromSeconds(60));
            return new RegistrationStartResult.EmailUnavailable();
        }

        // Delivery confirmed by provider: atomic finalize and invalidate older challenge for this email.
        var codeHmac = Hmac(challengeId, normalizedEmail, code);
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12);

        var activeKey = $"reg:email:{emailHash}:active";
        var challengeKey = $"reg:challenge:{challengeId}";

        const string finalizeScript = """
            local oldChallengeId = redis.call('GET', KEYS[1])
            if oldChallengeId and oldChallengeId ~= '' then
                redis.call('DEL', 'reg:challenge:' .. oldChallengeId)
            end
            redis.call('HSET', KEYS[3], 'email', ARGV[2], 'emailHash', ARGV[3], 'fullName', ARGV[4], 'codeHmac', ARGV[5], 'passwordHash', ARGV[6], 'challengeId', ARGV[1])
            redis.call('EXPIRE', KEYS[3], 600)
            redis.call('SET', KEYS[1], ARGV[1], 'EX', 600)
            redis.call('SET', KEYS[2], '1', 'EX', 60)
            redis.call('DEL', KEYS[4])
            return 1
            """;

        await db.ScriptEvaluateAsync(finalizeScript,
            [activeKey, cooldownKey, challengeKey, leaseKey],
            [challengeId, normalizedEmail, emailHash, fullName.Trim(), codeHmac, passwordHash]);

        return new RegistrationStartResult.Success(challengeId, 600, 60, "Mã xác thực đã được gửi đến email của bạn.");
    }

    public async Task<RegistrationVerifyResult> VerifyRegistrationAsync(string challengeId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(challengeId) || !IsHex32(challengeId.Trim()) ||
            string.IsNullOrWhiteSpace(code) || !IsAsciiDigits6(code.Trim()))
            return new RegistrationVerifyResult.InvalidOrExpired();

        var trimmedChallengeId = challengeId.Trim().ToLowerInvariant();
        var trimmedCode = code.Trim();

        var db = redis.GetDatabase();
        var challengeKey = $"reg:challenge:{trimmedChallengeId}";

        var storedEmail = (string?)await db.HashGetAsync(challengeKey, "email");
        var storedEmailHash = (string?)await db.HashGetAsync(challengeKey, "emailHash");

        if (string.IsNullOrEmpty(storedEmail) || string.IsNullOrEmpty(storedEmailHash))
            return new RegistrationVerifyResult.InvalidOrExpired();

        var expectedHmac = Hmac(trimmedChallengeId, storedEmail, trimmedCode);

        var activeKey = $"reg:email:{storedEmailHash}:active";
        var attemptsKey = $"reg:email:{storedEmailHash}:attempts";
        var lockKey = $"reg:email:{storedEmailHash}:lock";

        const string verifyScript = """
            if redis.call('EXISTS', KEYS[4]) == 1 then
                return { -1 }
            end
            local activeChallenge = redis.call('GET', KEYS[2])
            if not activeChallenge or activeChallenge ~= ARGV[2] then
                return { 0 }
            end
            local storedHmac = redis.call('HGET', KEYS[1], 'codeHmac')
            if not storedHmac then
                return { 0 }
            end
            if storedHmac == ARGV[1] then
                local email = redis.call('HGET', KEYS[1], 'email')
                local fullName = redis.call('HGET', KEYS[1], 'fullName')
                local passwordHash = redis.call('HGET', KEYS[1], 'passwordHash')
                redis.call('DEL', KEYS[1], KEYS[2], KEYS[3])
                return { 1, email, fullName, passwordHash }
            else
                local attempts = redis.call('INCR', KEYS[3])
                redis.call('EXPIRE', KEYS[3], 900)
                if attempts >= 5 then
                    redis.call('SET', KEYS[4], '1', 'EX', 900)
                    return { -2, tostring(attempts) }
                end
                return { -3, tostring(attempts) }
            end
            """;

        var res = (RedisResult[]?)await db.ScriptEvaluateAsync(verifyScript,
            [challengeKey, activeKey, attemptsKey, lockKey],
            [expectedHmac, trimmedChallengeId]);

        if (res is null || res.Length == 0)
            return new RegistrationVerifyResult.InvalidOrExpired();

        var codeStatus = (int)res[0];
        return codeStatus switch
        {
            1 => new RegistrationVerifyResult.Success((string)res[1]!, (string)res[2]!, (string)res[3]!),
            0 => new RegistrationVerifyResult.InvalidOrExpired(),
            -1 => new RegistrationVerifyResult.Locked(),
            -2 => new RegistrationVerifyResult.Locked(),
            -3 => new RegistrationVerifyResult.WrongCode(int.Parse((string)res[1]!)),
            _ => new RegistrationVerifyResult.InvalidOrExpired()
        };
    }
}
