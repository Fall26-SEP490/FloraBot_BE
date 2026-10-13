using Google.Apis.Auth;

namespace FloraBot.Api.Auth;

public interface IGoogleIdentityValidator
{
    Task<GoogleJsonWebSignature.Payload> ValidateAsync(string idToken, string audience, CancellationToken ct = default);
}

public sealed class GoogleIdentityValidator : IGoogleIdentityValidator
{
    public async Task<GoogleJsonWebSignature.Payload> ValidateAsync(string idToken, string audience, CancellationToken ct = default)
    {
        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [audience]
        };
        return await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
    }
}
