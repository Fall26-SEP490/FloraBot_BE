using FloraBot.Api.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace FloraBot.Api.Tests;

public sealed class OtpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task OtpIsBoundToKioskAndSingleUse()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OtpService>();
        var kiosk = Guid.NewGuid();
        var code = await service.IssueAsync(kiosk, "0901234567");
        Assert.NotNull(code);
        Assert.False(await service.VerifyAsync(Guid.NewGuid(), "0901234567", code));
        Assert.True(await service.VerifyAsync(kiosk, "0901234567", code));
        Assert.False(await service.VerifyAsync(kiosk, "0901234567", code));
    }
    [Fact]
    public async Task FiveFailuresLockEvenTheCorrectCodeAndResending()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OtpService>();
        var kiosk = Guid.NewGuid();
        var code = await service.IssueAsync(kiosk, "0901234567");
        Assert.NotNull(code);
        var incorrect = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++) Assert.False(await service.VerifyAsync(kiosk, "0901234567", incorrect));
        Assert.False(await service.VerifyAsync(kiosk, "0901234567", code));
        Assert.Null(await service.IssueAsync(kiosk, "0901234567"));
    }
}
