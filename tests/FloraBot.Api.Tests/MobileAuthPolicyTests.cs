using FloraBot.Api.Auth;

namespace FloraBot.Api.Tests;

public sealed class MobileAuthPolicyTests
{
    [Theory]
    [InlineData("CUSTOMER", true, false)]
    [InlineData("SELLER", true, false)]
    [InlineData("SELLER_STAFF", true, false)]
    [InlineData("TECHNICIAN", false, true)]
    [InlineData("ADMIN", false, false)]
    [InlineData("OPERATIONS_MANAGER", false, false)]
    public void StoredRoleDeterminesWhichLoginCanIssueASession(string role, bool member, bool technician)
    {
        Assert.True(MobileAuthPolicy.ValidLogin(new("member@example.invalid", "a password")));
        Assert.Equal(member, MobileAuthPolicy.AllowedLoginRole(role, technician: false));
        Assert.Equal(technician, MobileAuthPolicy.AllowedLoginRole(role, technician: true));
    }

    [Theory]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER_STAFF")]
    [InlineData("SELLER")]
    [InlineData("CUSTOMER")]
    public void MobileAdmitsOnlyTheFourSupportedPersonas(string role) =>
        Assert.True(MobileAuthPolicy.ValidLogin(new("member@example.invalid", "a password", role)));

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("KIOSK")]
    [InlineData("STAFF")]
    [InlineData("seller")]
    [InlineData("")]
    public void MobileCannotSelectAnUnsupportedOrElevatedRealm(string role) =>
        Assert.False(MobileAuthPolicy.ValidLogin(new("member@example.invalid", "a password", role)));

    [Fact]
    public void BcryptLimitUsesUtf8BytesAndRefreshCannotBeAnArbitraryInput()
    {
        Assert.False(MobileAuthPolicy.ValidLogin(new("a@b.invalid", new string('ầ', 25), "SELLER")));
        Assert.False(MobileAuthPolicy.ValidLogin(new(" ", "password", "CUSTOMER")));
        Assert.True(MobileAuthPolicy.ValidRefresh(new string('A', 64)));
        Assert.False(MobileAuthPolicy.ValidRefresh(new string('G', 64)));
        Assert.False(MobileAuthPolicy.ValidRefresh(null));
        Assert.False(MobileAuthPolicy.ValidRefresh(new string('A', 63)));
    }
}
