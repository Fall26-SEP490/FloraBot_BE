using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using FloraBot.Api.Data;
using Microsoft.AspNetCore.Authentication;
using FloraBot.Api.Modules.KioskOps;
using Microsoft.Extensions.Options;

namespace FloraBot.Api.Auth;

public sealed class KioskAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, FloraDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = Request.Headers["X-Kiosk-Key"].ToString();
        if (key.Length is < 8 or > 256) return AuthenticateResult.Fail("Invalid device credential.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var kiosk = await DeviceCredentials.FindEnabledAsync(db, hash, Context.RequestAborted);
        if (kiosk is null) return AuthenticateResult.Fail("Invalid device credential.");
        var identity = new ClaimsIdentity([new("sub", kiosk.Value.ToString()), new("role", "KIOSK"), new("kiosk_id", kiosk.Value.ToString())], Scheme.Name, "sub", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
