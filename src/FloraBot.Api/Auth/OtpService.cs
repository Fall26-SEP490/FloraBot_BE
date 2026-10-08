using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace FloraBot.Api.Auth;

public sealed class OtpService(IConnectionMultiplexer redis, IConfiguration configuration)
{
    private string Prefix(Guid kiosk, string phone) => "otp:" + kiosk + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(phone)));
    private string Hash(string code) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(configuration["JWT_SIGNING_KEY"]!), Encoding.UTF8.GetBytes("otp:" + code)));
    public async Task<string?> IssueAsync(Guid kiosk, string phone)
    {
        var prefix = Prefix(kiosk, phone);
        var code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6");
        var result = await redis.GetDatabase().ScriptEvaluateAsync("""
            if redis.call('EXISTS', KEYS[1]..':lock') == 1 or redis.call('EXISTS', KEYS[1]..':cooldown') == 1 then return 0 end
            redis.call('SET', KEYS[1]..':code', ARGV[1], 'EX', 180)
            redis.call('SET', KEYS[1]..':cooldown', '1', 'EX', 60)
            return 1
            """, [prefix], [Hash(code)]);
        return (int)result == 1 ? code : null;
    }
    public async Task<bool> VerifyAsync(Guid kiosk, string phone, string code)
    {
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) return false;
        var result = await redis.GetDatabase().ScriptEvaluateAsync("""
            if redis.call('EXISTS', KEYS[1]..':lock') == 1 then return 0 end
            local saved = redis.call('GET', KEYS[1]..':code')
            if not saved then return 0 end
            if saved == ARGV[1] then
              redis.call('DEL', KEYS[1]..':code', KEYS[1]..':attempts')
              return 1
            end
            local attempts = redis.call('INCR', KEYS[1]..':attempts')
            redis.call('EXPIRE', KEYS[1]..':attempts', 900)
            if attempts >= 5 then
              redis.call('SET', KEYS[1]..':lock', '1', 'EX', 900)
              redis.call('DEL', KEYS[1]..':code')
            end
            return 0
            """, [Prefix(kiosk, phone)], [Hash(code)]);
        return (int)result == 1;
    }
}
