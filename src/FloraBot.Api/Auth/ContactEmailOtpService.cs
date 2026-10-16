using System.Security.Cryptography;
using System.Text;
using FloraBot.Api.Modules.Notify;
using StackExchange.Redis;

namespace FloraBot.Api.Auth;

public abstract record ContactEmailStartResult
{
    public sealed record Success(string ChallengeId, int ExpiresInSeconds, int ResendAfterSeconds, string Message) : ContactEmailStartResult;
    public sealed record Cooldown(int RetryAfterSeconds) : ContactEmailStartResult;
    public sealed record Locked() : ContactEmailStartResult;
    public sealed record EmailUnavailable() : ContactEmailStartResult;
    public sealed record Superseded() : ContactEmailStartResult;
}

public abstract record ContactEmailVerifyResult
{
    public sealed record Success(string VerifiedEmail) : ContactEmailVerifyResult;
    public sealed record InvalidOrExpired() : ContactEmailVerifyResult;
    public sealed record WrongCode(int Attempts) : ContactEmailVerifyResult;
    public sealed record Locked() : ContactEmailVerifyResult;
}

public sealed class ContactEmailOtpService(
    IConnectionMultiplexer redis,
    IConfiguration configuration,
    ITransactionalEmailSender emailSender,
    EmailSettings emailSettings)
{
    private static bool IsHex32(string s) =>
        s.Length == 32 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static bool IsAsciiDigits6(string s) =>
        s.Length == 6 && s.All(c => c is >= '0' and <= '9');

    private string Hmac(Guid userId, string challengeId, string email, string code)
    {
        var key = Encoding.UTF8.GetBytes(configuration["JWT_SIGNING_KEY"]!);
        var message = Encoding.UTF8.GetBytes($"contact-otp:{userId}:{challengeId.Trim().ToLowerInvariant()}:{email.Trim().ToLowerInvariant()}:{code.Trim()}");
        return Convert.ToHexString(HMACSHA256.HashData(key, message));
    }

    public async Task<ContactEmailStartResult> StartContactEmailAsync(Guid userId, string targetEmail, string? userDisplayName, CancellationToken ct)
    {
        if (emailSettings.Provider == EmailProvider.Disabled)
            return new ContactEmailStartResult.EmailUnavailable();

        var normalizedEmail = targetEmail.Trim().ToLowerInvariant();
        var db = redis.GetDatabase();

        var lockKey = $"contact:user:{userId}:lock";
        var cooldownKey = $"contact:user:{userId}:cooldown";
        var leaseKey = $"contact:user:{userId}:lease";

        var leaseToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        const string reserveScript = """
            if redis.call('EXISTS', KEYS[1]) == 1 then
                return -1
            end
            if redis.call('EXISTS', KEYS[2]) == 1 or redis.call('EXISTS', KEYS[3]) == 1 then
                return -2
            end
            redis.call('SET', KEYS[3], ARGV[1], 'EX', 60)
            redis.call('SET', KEYS[2], '1', 'EX', 60)
            return 1
            """;

        var reserveResult = (int)await db.ScriptEvaluateAsync(reserveScript, [lockKey, cooldownKey, leaseKey], [leaseToken]);
        if (reserveResult == -1)
            return new ContactEmailStartResult.Locked();
        if (reserveResult == -2)
            return new ContactEmailStartResult.Cooldown(60);

        var challengeId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
        var subject = "FloraBot: Xác thực email liên hệ mới";
        var text = $"Mã xác thực email liên hệ của bạn là: {code}\nMã có hiệu lực trong 10 phút. Không chia sẻ mã này với bất kỳ ai.";
        var html = EmailTemplates.ContactEmailOtp(code, userDisplayName);

        try
        {
            await emailSender.SendAsync(new TransactionalEmail(normalizedEmail, subject, text, html), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            const string cleanupScript = """
                if redis.call('GET', KEYS[1]) == ARGV[1] then
                    redis.call('SET', KEYS[2], '1', 'EX', 60)
                    redis.call('DEL', KEYS[1])
                    return 1
                end
                return 0
                """;
            await db.ScriptEvaluateAsync(cleanupScript, [leaseKey, cooldownKey], [leaseToken]);
            return new ContactEmailStartResult.EmailUnavailable();
        }

        var codeHmac = Hmac(userId, challengeId, normalizedEmail, code);
        var activeKey = $"contact:user:{userId}:active";
        var challengeKey = $"contact:challenge:{challengeId}";

        const string finalizeScript = """
            if redis.call('GET', KEYS[4]) ~= ARGV[5] then
                return 0
            end
            local oldChallengeId = redis.call('GET', KEYS[1])
            if oldChallengeId and oldChallengeId ~= '' then
                redis.call('DEL', 'contact:challenge:' .. oldChallengeId)
            end
            redis.call('HSET', KEYS[3], 'userId', ARGV[2], 'targetEmail', ARGV[3], 'codeHmac', ARGV[4], 'challengeId', ARGV[1])
            redis.call('EXPIRE', KEYS[3], 600)
            redis.call('SET', KEYS[1], ARGV[1], 'EX', 600)
            redis.call('SET', KEYS[2], '1', 'EX', 60)
            redis.call('DEL', KEYS[4])
            return 1
            """;

        var finalizeResult = (int)await db.ScriptEvaluateAsync(finalizeScript,
            [activeKey, cooldownKey, challengeKey, leaseKey],
            [challengeId, userId.ToString(), normalizedEmail, codeHmac, leaseToken]);

        if (finalizeResult == 0)
        {
            // The lease was superseded or invalidated (e.g. by reset while SMTP was in flight)
            return new ContactEmailStartResult.Superseded();
        }

        return new ContactEmailStartResult.Success(challengeId, 600, 60, "Mã xác thực đã được gửi đến email liên hệ mới.");
    }

    public async Task<ContactEmailVerifyResult> VerifyContactEmailAsync(Guid userId, string challengeId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(challengeId) || !IsHex32(challengeId.Trim()) ||
            string.IsNullOrWhiteSpace(code) || !IsAsciiDigits6(code.Trim()))
            return new ContactEmailVerifyResult.InvalidOrExpired();

        var trimmedChallengeId = challengeId.Trim().ToLowerInvariant();
        var trimmedCode = code.Trim();

        var db = redis.GetDatabase();
        var challengeKey = $"contact:challenge:{trimmedChallengeId}";

        var storedUserIdStr = (string?)await db.HashGetAsync(challengeKey, "userId");
        var storedTargetEmail = (string?)await db.HashGetAsync(challengeKey, "targetEmail");

        if (string.IsNullOrEmpty(storedUserIdStr) || string.IsNullOrEmpty(storedTargetEmail) ||
            !Guid.TryParse(storedUserIdStr, out var storedUserId) || storedUserId != userId)
            return new ContactEmailVerifyResult.InvalidOrExpired();

        var expectedHmac = Hmac(userId, trimmedChallengeId, storedTargetEmail, trimmedCode);

        var activeKey = $"contact:user:{userId}:active";
        var attemptsKey = $"contact:user:{userId}:attempts";
        var lockKey = $"contact:user:{userId}:lock";

        const string verifyScript = """
            if redis.call('EXISTS', KEYS[4]) == 1 then
                return { -1 }
            end
            local activeChallenge = redis.call('GET', KEYS[2])
            if not activeChallenge or activeChallenge ~= ARGV[2] then
                return { 0 }
            end
            local storedUserId = redis.call('HGET', KEYS[1], 'userId')
            if not storedUserId or storedUserId ~= ARGV[3] then
                return { 0 }
            end
            local storedHmac = redis.call('HGET', KEYS[1], 'codeHmac')
            if not storedHmac then
                return { 0 }
            end
            if storedHmac == ARGV[1] then
                local targetEmail = redis.call('HGET', KEYS[1], 'targetEmail')
                redis.call('DEL', KEYS[1], KEYS[2], KEYS[3])
                return { 1, targetEmail }
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
            [expectedHmac, trimmedChallengeId, userId.ToString()]);

        if (res is null || res.Length == 0)
            return new ContactEmailVerifyResult.InvalidOrExpired();

        var codeStatus = (int)res[0];
        return codeStatus switch
        {
            1 => new ContactEmailVerifyResult.Success((string)res[1]!),
            0 => new ContactEmailVerifyResult.InvalidOrExpired(),
            -1 => new ContactEmailVerifyResult.Locked(),
            -2 => new ContactEmailVerifyResult.Locked(),
            -3 => new ContactEmailVerifyResult.WrongCode(int.Parse((string)res[1]!)),
            _ => new ContactEmailVerifyResult.InvalidOrExpired()
        };
    }

    public async Task ResetContactEmailAsync(Guid userId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var activeKey = $"contact:user:{userId}:active";
        var leaseKey = $"contact:user:{userId}:lease";

        const string resetScript = """
            local oldChallengeId = redis.call('GET', KEYS[1])
            if oldChallengeId and oldChallengeId ~= '' then
                redis.call('DEL', 'contact:challenge:' .. oldChallengeId)
            end
            redis.call('DEL', KEYS[1])
            redis.call('DEL', KEYS[2])
            return 1
            """;

        await db.ScriptEvaluateAsync(resetScript, [activeKey, leaseKey]);
    }
}
