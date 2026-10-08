using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FloraBot.Api.Data.Entities;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

namespace FloraBot.Api.Auth;

public sealed class TokenService(IConfiguration configuration, IConnectionMultiplexer redis)
{
    public const string Issuer = "FloraBot";
    public static string CredentialVersion(User user) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.PasswordHash ?? "")));
    public string Issue(User user, string? sellerStatus = null, string? kioskId = null, DateTime? expires = null)
    {
        var claims = new List<Claim> { new("sub", user.Id.ToString()), new("role", user.Role), new("name", user.FullName) };
        if (user.PasswordHash is not null) claims.Add(new("credential_version", CredentialVersion(user)));
        if (user.SellerId is not null) { claims.Add(new("seller_id", user.SellerId.ToString()!)); claims.Add(new("seller_status", sellerStatus ?? "PENDING")); }
        if (kioskId is not null) claims.Add(new("kiosk_id", kioskId));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["JWT_SIGNING_KEY"]!)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, Issuer, claims,
            expires: expires ?? DateTime.UtcNow.AddMinutes(kioskId is not null || user.Role == "CUSTOMER" ? 10 : 15), signingCredentials: credentials));
    }
    public async Task<string> CreateRefresh(User user)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await redis.GetDatabase().StringSetAsync(Key(token), user.Id + ":" + CredentialVersion(user), TimeSpan.FromDays(7));
        return token;
    }
    public async Task<(Guid Id, string Version)?> ConsumeRefresh(string token)
    {
        if (token.Length != 64) return null;
        var value = await redis.GetDatabase().StringGetDeleteAsync(Key(token));
        var parts = value.ToString().Split(':');
        return parts.Length == 2 && Guid.TryParse(parts[0], out var id) ? (id, parts[1]) : null;
    }
    private static string Key(string token) => "refresh:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
