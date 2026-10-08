using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SellerBankTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task BankDetailsAreProtectedMaskedAndCopiedAtWithdrawalTime()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("bank-account-v1");
        var seller = Guid.NewGuid(); var owner = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone) VALUES(@seller,'Bank test','0901112233');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@owner,@owner::text || '@example.invalid','!unprovisioned','Bank test','SELLER',@seller);
            INSERT INTO payment.ledger_entries(journal_id,account,seller_id,amount,ref_type,ref_id,memo)
            VALUES(@seller,'GATEWAY_CLEARING',NULL,200000,'ORDER',@seller,'Bank fixture'),
                  (@seller,'SELLER_AVAILABLE',@seller,-200000,'ORDER',@seller,'Bank fixture');
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("owner", owner); await setup.ExecuteNonQueryAsync();
        using var shop = factory.CreateClient(); shop.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: owner, sellerId: seller));
        var path = $"/api/sellers/{seller}";
        Assert.Equal("MISSING", (await shop.GetFromJsonAsync<SellerBankResponse>(path + "/bank"))!.Status);
        const string account = "00123456789";
        using var saved = await shop.PostAsJsonAsync(path + "/flows/set_seller_bank", new { p_bank = " Test Bank ", p_holder = " test shop ", p_account_enc = account });
        saved.EnsureSuccessStatusCode();
        await using var stored = data.CreateCommand("SELECT bank_account_no_enc FROM identity.sellers WHERE id=@seller");
        stored.Parameters.AddWithValue("seller", seller);
        var ciphertext = (string)(await stored.ExecuteScalarAsync())!;
        Assert.NotEqual(account, ciphertext); Assert.Equal(account, protector.Unprotect(ciphertext));
        using var response = await shop.GetAsync(path + "/bank");
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(account, json); Assert.DoesNotContain(ciphertext, json);
        var bank = JsonSerializer.Deserialize<SellerBankResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("READY", bank.Status); Assert.Equal("Test Bank", bank.BankName); Assert.Equal("TEST SHOP", bank.Holder); Assert.Equal("6789", bank.AccountSuffix);
        using var withdrawal = await shop.PostAsJsonAsync(path + "/flows/request_withdrawal", new { p_amount = 100000 });
        withdrawal.EnsureSuccessStatusCode();
        var id = (await withdrawal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        (await shop.PostAsJsonAsync(path + "/flows/set_seller_bank", new { p_bank = "Other Bank", p_holder = "new holder", p_account_enc = "9876" })).EnsureSuccessStatusCode();
        Assert.Equal("76", (await shop.GetFromJsonAsync<SellerBankResponse>(path + "/bank"))!.AccountSuffix);
        await using var snapshot = data.CreateCommand("SELECT bank_account_no_enc FROM payment.withdrawal_requests WHERE id=@id");
        snapshot.Parameters.AddWithValue("id", id); Assert.Equal(account, protector.Unprotect((string)(await snapshot.ExecuteScalarAsync())!));
        await using var audit = data.CreateCommand("SELECT payload::text FROM notify.audit_logs WHERE entity_id=@seller AND action='SELLER_BANK_CHANGED' ORDER BY id");
        audit.Parameters.AddWithValue("seller", seller);
        await using (var reader = await audit.ExecuteReaderAsync())
            while (await reader.ReadAsync()) { Assert.DoesNotContain(account, reader.GetString(0)); Assert.DoesNotContain(ciphertext, reader.GetString(0)); }
        using var foreign = factory.CreateClient(); foreign.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(path + "/bank")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.PostAsJsonAsync(path + "/flows/set_seller_bank", new { p_bank = "Bank", p_holder = "Other", p_account_enc = "123456" })).StatusCode);
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path + "/bank")).StatusCode);
        await using var corrupt = data.CreateCommand("UPDATE identity.sellers SET bank_account_no_enc='legacy-plaintext-test' WHERE id=@seller");
        corrupt.Parameters.AddWithValue("seller", seller); await corrupt.ExecuteNonQueryAsync();
        bank = (await shop.GetFromJsonAsync<SellerBankResponse>(path + "/bank"))!;
        Assert.Equal("NEEDS_UPDATE", bank.Status); Assert.Null(bank.AccountSuffix);
        var wallet = await shop.GetFromJsonAsync<FloraBot.Api.Modules.Payment.WalletResponse>(path + "/wallet");
        Assert.False(wallet!.BankConfigured);
    }

    [Theory]
    [InlineData("", "123456", "Holder")]
    [InlineData("Bank", "", "Holder")]
    [InlineData("Bank", "123", "Holder")]
    [InlineData("Bank", "123 456", "Holder")]
    [InlineData("Bank", "123456", "\n")]
    [InlineData("Bank\nInjected", "123456", "Holder")]
    public async Task InvalidInputDoesNotUpdateBank(string bank, string account, string holder)
    {
        using var shop = factory.CreateClient(); shop.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var response = await shop.PostAsJsonAsync("/api/sellers/20000000-0000-0000-0000-000000000001/flows/set_seller_bank", new { p_bank = bank, p_account_enc = account, p_holder = holder });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
