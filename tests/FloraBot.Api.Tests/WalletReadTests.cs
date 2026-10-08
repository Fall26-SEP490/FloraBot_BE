using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class WalletReadTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task WalletUsesLedgerAndShowsWithdrawalWithoutExposingBankAccount()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var owner = Guid.NewGuid();
        await using var fixture = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,bank_name,bank_account_no_enc,bank_holder)
            VALUES(@seller,'Wallet test','0901112233','APPROVED','20000000-0000-0000-0000-00000000000a','Private Test Bank',@account,'PRIVATE HOLDER');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@owner,@owner::text || '@example.invalid','!unprovisioned','Wallet owner','SELLER',@seller);
            INSERT INTO payment.ledger_entries(journal_id,account,seller_id,amount,ref_type,ref_id,memo)
            VALUES(@seller,'GATEWAY_CLEARING',NULL,300000,'ORDER',@seller,'Wallet fixture'),
                  (@seller,'SELLER_AVAILABLE',@seller,-200000,'ORDER',@seller,'Wallet fixture'),
                  (@seller,'SELLER_PENDING',@seller,-100000,'ORDER',@seller,'Wallet fixture');
            """);
        fixture.Parameters.AddWithValue("seller", seller); fixture.Parameters.AddWithValue("owner", owner);
        fixture.Parameters.AddWithValue("account", scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("bank-account-v1").Protect("00987654321"));
        await fixture.ExecuteNonQueryAsync();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: owner, sellerId: seller));
        var path = $"/api/sellers/{seller}/wallet";
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode(); Assert.True(response.Headers.CacheControl?.NoStore);
        var wallet = (await response.Content.ReadFromJsonAsync<WalletResponse>())!;
        Assert.Equal(200000, wallet.Balance!.AvailableBalance); Assert.Equal(100000, wallet.Balance.PendingBalance);
        Assert.Equal(0, wallet.Balance.Debt); Assert.True(wallet.BankConfigured); Assert.Empty(wallet.Withdrawals);
        Assert.Equal(2, wallet.Entries.Count); Assert.False(wallet.HasMoreEntries); Assert.False(wallet.HasMoreWithdrawals);
        await using var minimum = data.CreateCommand("SELECT flow.cfg('withdraw_min')");
        Assert.Equal(Convert.ToInt64(await minimum.ExecuteScalarAsync()), wallet.MinimumWithdrawal);
        using var requested = await client.PostAsJsonAsync($"/api/sellers/{seller}/flows/request_withdrawal", new { p_amount = 100000 });
        requested.EnsureSuccessStatusCode();
        var id = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var json = await client.GetStringAsync(path);
        Assert.DoesNotContain("00987654321", json); Assert.DoesNotContain("PRIVATE HOLDER", json);
        Assert.DoesNotContain("Private Test Bank", json); Assert.DoesNotContain("bankAccount", json, StringComparison.OrdinalIgnoreCase);
        wallet = JsonSerializer.Deserialize<WalletResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var row = Assert.Single(wallet.Withdrawals);
        Assert.Equal(id, row.Id); Assert.Equal(100000, row.Amount); Assert.Equal("PENDING", row.Status); Assert.Null(row.PaidAt);
        // Requesting does not reserve ledger funds in the authoritative source flow.
        Assert.Equal(200000, wallet.Balance!.AvailableBalance); Assert.Equal(2, wallet.Entries.Count);
        using var foreign = factory.CreateClient(); foreign.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(path)).StatusCode);
        using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/sellers/{Guid.NewGuid()}/wallet")).StatusCode);
    }

    [Fact]
    public async Task EmptyWalletHasNoInventedFundsOrBankDetails()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid();
        await using var fixture = data.CreateCommand("INSERT INTO identity.sellers(id,shop_name,phone) VALUES(@seller,'Empty wallet','0901112233')");
        fixture.Parameters.AddWithValue("seller", seller); await fixture.ExecuteNonQueryAsync();
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var wallet = (await admin.GetFromJsonAsync<WalletResponse>($"/api/sellers/{seller}/wallet"))!;
        Assert.Null(wallet.Balance); Assert.Empty(wallet.Entries); Assert.Empty(wallet.Withdrawals); Assert.False(wallet.BankConfigured);
    }
}
