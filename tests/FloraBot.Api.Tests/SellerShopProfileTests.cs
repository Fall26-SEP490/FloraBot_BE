using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace FloraBot.Api.Tests;

public sealed class SellerShopProfileTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string ProfilePath = "/api/me/shop";
    private const string BankAccount = "00123456789";
    private const string Address = "123 Shop Profile Street";
    private NpgsqlDataSource Data => factory.Services.GetRequiredService<NpgsqlDataSource>();

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("PAST_DUE")]
    public async Task OwnerCanReadAndPatchProfileWithoutChangingStatusAddressOrBank(string status)
    {
        var fixture = await CreateFixture(status);
        using var client = Client(fixture);

        using var read = await client.GetAsync(ProfilePath);
        var original = await Profile(read, fixture);
        Assert.Equal(fixture.ShopName, original.ShopName);
        Assert.Equal(fixture.Phone, original.Phone);
        Assert.Equal(fixture.ContactEmail, original.ContactEmail);
        Assert.Equal(status, original.Status);

        using var patch = await client.PatchAsJsonAsync(ProfilePath, new
        {
            shopName = " Updated profile shop ",
            phone = " 0901234567 ",
            contactEmail = " " + fixture.VerifiedEmail.ToUpperInvariant() + " "
        });
        var updated = await Profile(patch, fixture);
        Assert.Equal("Updated profile shop", updated.ShopName);
        Assert.Equal("0901234567", updated.Phone);
        Assert.Equal(fixture.VerifiedEmail, updated.ContactEmail);
        Assert.Equal(status, updated.Status);

        var stored = await State(fixture);
        Assert.Equal(updated.ShopName, stored.ShopName);
        Assert.Equal(updated.Phone, stored.Phone);
        Assert.Equal(updated.ContactEmail, stored.ContactEmail);
        Assert.Equal(status, stored.Status);
        Assert.Equal(Address, stored.Address);
        Assert.Equal("Fixture Bank", stored.BankName);
        Assert.Equal("PROFILE OWNER", stored.BankHolder);
        Assert.Equal(fixture.Ciphertext, stored.Ciphertext);
        Assert.Equal(fixture.VerifiedEmail, stored.VerifiedEmail);
        Assert.Equal(fixture.VerifiedAt, stored.VerifiedAt);
        await AssertAudit(fixture, nameChanged: true, phoneChanged: true, emailChanged: true);

        using var reread = await client.GetAsync(ProfilePath);
        Assert.Equal(updated, await Profile(reread, fixture));
    }

    [Fact]
    public async Task PastDuePartialPatchPreservesOmittedPhoneAndEmail()
    {
        var fixture = await CreateFixture("PAST_DUE");
        using var client = Client(fixture);
        var before = await State(fixture);

        using var patch = await client.PatchAsJsonAsync(ProfilePath, new { shopName = "Renamed expired shop" });
        var updated = await Profile(patch, fixture);
        Assert.Equal("Renamed expired shop", updated.ShopName);
        Assert.Equal(fixture.Phone, updated.Phone);
        Assert.Equal(fixture.ContactEmail, updated.ContactEmail);
        Assert.Equal("PAST_DUE", updated.Status);
        Assert.Equal(before with { ShopName = updated.ShopName }, await State(fixture));
        await AssertAudit(fixture, nameChanged: true, phoneChanged: false, emailChanged: false);
    }

    [Fact]
    public async Task PastDueEmailUpdateRequiresOwnersVerifiedEmailAndRejectsAtomically()
    {
        var fixture = await CreateFixture("PAST_DUE");
        var other = await CreateFixture("ACTIVE");
        using var client = Client(fixture);
        var before = await State(fixture);

        // A login email and another owner's verified contact email do not verify this owner's shop email.
        foreach (var email in new[] { fixture.LoginEmail, other.VerifiedEmail })
        {
            using var rejected = await client.PatchAsJsonAsync(ProfilePath, new
            {
                shopName = "Must not be saved",
                phone = "0907654321",
                contactEmail = email
            });
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            Assert.True(rejected.Headers.CacheControl?.NoStore);
            Assert.Equal(before, await State(fixture));
            await AssertNoProfileAudit(fixture);
        }
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("APPROVED")]
    [InlineData("SUSPENDED")]
    [InlineData("CLOSED")]
    public async Task OtherSellerStatesCannotReadOrPatchProfile(string status)
    {
        var fixture = await CreateFixture(status);
        using var client = Client(fixture);
        await AssertReadAndPatchDenied(client, HttpStatusCode.Forbidden, fixture);
    }

    [Theory]
    [InlineData("LOCKED")]
    [InlineData("DISABLED")]
    public async Task InactiveOwnerSessionCannotReadOrPatchPastDueProfile(string userStatus)
    {
        var fixture = await CreateFixture("PAST_DUE");
        using var client = Client(fixture);
        await using (var disable = Data.CreateCommand("UPDATE identity.users SET status=@status WHERE id=@owner"))
        {
            disable.Parameters.AddWithValue("status", userStatus);
            disable.Parameters.AddWithValue("owner", fixture.Owner);
            await disable.ExecuteNonQueryAsync();
        }
        await AssertReadAndPatchDenied(client, HttpStatusCode.Unauthorized, fixture);
    }

    [Fact]
    public async Task ForeignSellerClaimCannotReadOrPatchEitherShop()
    {
        var owner = await CreateFixture("PAST_DUE");
        var other = await CreateFixture("PAST_DUE");
        var otherBefore = await State(other);
        using var client = Client(owner, sellerClaim: other.Seller);

        await AssertReadAndPatchDenied(client, HttpStatusCode.Unauthorized, owner);
        Assert.Equal(otherBefore, await State(other));
        await AssertNoProfileAudit(other);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("CUSTOMER")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER_STAFF")]
    public async Task NonSellerRolesCannotReadOrPatchPastDueProfile(string role)
    {
        var fixture = await CreateFixture("PAST_DUE", role);
        using var client = Client(fixture);
        await AssertReadAndPatchDenied(client, HttpStatusCode.Forbidden, fixture);
    }

    [Fact]
    public async Task PastDueOwnerCanSetProtectedBankWithoutReactivatingShop()
    {
        var fixture = await CreateFixture("PAST_DUE");
        using var client = Client(fixture);
        var before = await State(fixture);
        const string account = "00987654321";
        var path = $"/api/sellers/{fixture.Seller}";

        using var save = await client.PostAsJsonAsync(path + "/flows/set_seller_bank", new
        {
            p_bank = " Updated Bank ",
            p_holder = " updated holder ",
            p_account_enc = account
        });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var stored = await State(fixture);
        Assert.NotEqual(account, stored.Ciphertext);
        Assert.NotEqual(fixture.Ciphertext, stored.Ciphertext);
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("bank-account-v1");
        Assert.Equal(account, protector.Unprotect(stored.Ciphertext));
        Assert.Equal(before with
        {
            BankName = "Updated Bank",
            BankHolder = "UPDATED HOLDER",
            Ciphertext = stored.Ciphertext
        }, stored);

        using var read = await client.GetAsync(path + "/bank");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.True(read.Headers.CacheControl?.NoStore);
        var json = await read.Content.ReadAsStringAsync();
        Assert.DoesNotContain(account, json);
        Assert.DoesNotContain(stored.Ciphertext, json);
        var bank = await read.Content.ReadFromJsonAsync<SellerBankResponse>();
        Assert.NotNull(bank);
        Assert.Equal("READY", bank.Status);
        Assert.Equal("Updated Bank", bank.BankName);
        Assert.Equal("UPDATED HOLDER", bank.Holder);
        Assert.Equal("4321", bank.AccountSuffix);

        await using var audit = Data.CreateCommand("SELECT actor_id,payload::text FROM notify.audit_logs WHERE entity_id=@seller AND action='SELLER_BANK_CHANGED'");
        audit.Parameters.AddWithValue("seller", fixture.Seller);
        await using var reader = await audit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(fixture.Owner, reader.GetGuid(0));
        Assert.DoesNotContain(account, reader.GetString(1));
        Assert.DoesNotContain(stored.Ciphertext, reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    private async Task<Fixture> CreateFixture(string status, string role = "SELLER")
    {
        var seller = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var ciphertext = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("bank-account-v1").Protect(BankAccount);
        var fixture = new Fixture(seller, owner, role, "Profile shop " + seller.ToString("N")[..8],
            "0901112233", $"shop-{seller:N}@example.invalid", $"login-{owner:N}@example.invalid",
            $"verified-{owner:N}@example.invalid", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), ciphertext);
        await using var setup = Data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,address,status,package_id,package_expires_at,
              package_expires_at_ts,contact_email,bank_name,bank_account_no_enc,bank_holder)
            VALUES(@seller,@shop,@phone,@address,@status,
              (SELECT id FROM identity.subscription_packages WHERE status='ACTIVE' ORDER BY id LIMIT 1),
              CASE WHEN @status='PAST_DUE' THEN CURRENT_DATE-1 ELSE CURRENT_DATE+30 END,
              CASE WHEN @status='PAST_DUE' THEN public.app_now()-interval '1 day' ELSE public.app_now()+interval '30 days' END,
              @contact,'Fixture Bank',@ciphertext,'PROFILE OWNER');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@owner,@login,'!unprovisioned','Profile test owner',@role,@user_seller,'ACTIVE');
            INSERT INTO identity.user_profiles(user_id,contact_email,contact_email_verified_at)
            VALUES(@owner,@verified_email,@verified_at);
            """);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("shop", fixture.ShopName);
        setup.Parameters.AddWithValue("phone", fixture.Phone);
        setup.Parameters.AddWithValue("address", Address);
        setup.Parameters.AddWithValue("status", status);
        setup.Parameters.AddWithValue("contact", fixture.ContactEmail);
        setup.Parameters.AddWithValue("ciphertext", ciphertext);
        setup.Parameters.AddWithValue("owner", owner);
        setup.Parameters.AddWithValue("login", fixture.LoginEmail);
        setup.Parameters.AddWithValue("role", role);
        setup.Parameters.Add("user_seller", NpgsqlDbType.Uuid).Value = role is "SELLER" or "SELLER_STAFF" ? seller : DBNull.Value;
        setup.Parameters.AddWithValue("verified_email", fixture.VerifiedEmail);
        setup.Parameters.AddWithValue("verified_at", fixture.VerifiedAt);
        await setup.ExecuteNonQueryAsync();
        return fixture;
    }

    private HttpClient Client(Fixture fixture, Guid? sellerClaim = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            factory.Token(fixture.Role, userId: fixture.Owner, sellerId: sellerClaim ?? fixture.Seller));
        return client;
    }

    private static async Task<SellerShopProfileResponse> Profile(HttpResponseMessage response, Fixture fixture)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(BankAccount, json);
        Assert.DoesNotContain(fixture.Ciphertext, json);
        Assert.DoesNotContain("bankAccountNoEnc", json, StringComparison.OrdinalIgnoreCase);
        var profile = await response.Content.ReadFromJsonAsync<SellerShopProfileResponse>();
        Assert.NotNull(profile);
        Assert.Equal(fixture.Seller, profile.Id);
        Assert.Equal(Address, profile.Address);
        return profile;
    }

    private async Task<ShopState> State(Fixture fixture)
    {
        await using var command = Data.CreateCommand("""
            SELECT s.shop_name,s.phone,s.contact_email,s.address,s.status,s.bank_name,s.bank_holder,
              s.bank_account_no_enc,p.contact_email,p.contact_email_verified_at
            FROM identity.sellers s JOIN identity.user_profiles p ON p.user_id=@owner WHERE s.id=@seller
            """);
        command.Parameters.AddWithValue("owner", fixture.Owner);
        command.Parameters.AddWithValue("seller", fixture.Seller);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetDateTime(9));
    }

    private async Task AssertReadAndPatchDenied(HttpClient client, HttpStatusCode expected, Fixture fixture)
    {
        var before = await State(fixture);
        using var read = await client.GetAsync(ProfilePath);
        Assert.Equal(expected, read.StatusCode);
        using var patch = await client.PatchAsJsonAsync(ProfilePath, new { shopName = "Must not be saved" });
        Assert.Equal(expected, patch.StatusCode);
        Assert.Equal(before, await State(fixture));
        await AssertNoProfileAudit(fixture);
    }

    private async Task AssertNoProfileAudit(Fixture fixture)
    {
        await using var command = Data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE entity_id=@seller AND action='SELLER_SHOP_PROFILE_UPDATED'");
        command.Parameters.AddWithValue("seller", fixture.Seller);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    private async Task AssertAudit(Fixture fixture, bool nameChanged, bool phoneChanged, bool emailChanged)
    {
        await using var command = Data.CreateCommand("SELECT actor_id,entity_type,payload::text FROM notify.audit_logs WHERE entity_id=@seller AND action='SELLER_SHOP_PROFILE_UPDATED'");
        command.Parameters.AddWithValue("seller", fixture.Seller);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(fixture.Owner, reader.GetGuid(0));
        Assert.Equal("sellers", reader.GetString(1));
        var json = reader.GetString(2);
        Assert.DoesNotContain(BankAccount, json);
        Assert.DoesNotContain(fixture.Ciphertext, json);
        using var payload = JsonDocument.Parse(json);
        Assert.Equal(3, payload.RootElement.EnumerateObject().Count());
        Assert.Equal(nameChanged, payload.RootElement.GetProperty("name_changed").GetBoolean());
        Assert.Equal(phoneChanged, payload.RootElement.GetProperty("phone_changed").GetBoolean());
        Assert.Equal(emailChanged, payload.RootElement.GetProperty("email_changed").GetBoolean());
        Assert.False(await reader.ReadAsync());
    }

    private sealed record Fixture(Guid Seller, Guid Owner, string Role, string ShopName, string Phone,
        string ContactEmail, string LoginEmail, string VerifiedEmail, DateTime VerifiedAt, string Ciphertext);

    private sealed record ShopState(string ShopName, string Phone, string ContactEmail, string Address,
        string Status, string BankName, string BankHolder, string Ciphertext, string VerifiedEmail, DateTime VerifiedAt);
}
