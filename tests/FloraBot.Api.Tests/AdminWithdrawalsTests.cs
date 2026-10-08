using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdminWithdrawalsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task DestinationAccessIsExplicitAuditedDistinctAndUsesWithdrawalSnapshot()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("bank-account-v1");
        var seller = Guid.NewGuid(); var owner = Guid.NewGuid(); var payer = Guid.NewGuid();
        const string account = "000987654321";
        var encrypted = protector.Protect(account);
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,bank_name,bank_account_no_enc,bank_holder)
            VALUES(@seller,'Payout read fixture','0901112233','APPROVED','20000000-0000-0000-0000-00000000000a','Snapshot Bank',@encrypted,'SNAPSHOT HOLDER');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@owner,@owner::text || '@example.invalid','!unprovisioned','Owner','SELLER',@seller),
            (@payer,@payer::text || '@example.invalid','!unprovisioned','Payer','ADMIN',NULL);
            INSERT INTO payment.ledger_entries(journal_id,account,seller_id,amount,ref_type,ref_id,memo)
            VALUES(@seller,'GATEWAY_CLEARING',NULL,200000,'ORDER',@seller,'Fixture'),(@seller,'SELLER_AVAILABLE',@seller,-200000,'ORDER',@seller,'Fixture');
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("owner", owner); setup.Parameters.AddWithValue("payer", payer); setup.Parameters.AddWithValue("encrypted", encrypted);
        await setup.ExecuteNonQueryAsync();
        using var shop = factory.CreateClient(); shop.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: owner, sellerId: seller));
        using var first = factory.CreateClient(); first.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        using var second = factory.CreateClient(); second.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: payer));
        using var anonymous = factory.CreateClient();
        var requested = await shop.PostAsJsonAsync($"/api/sellers/{seller}/flows/request_withdrawal", new { p_amount = 100000 });
        requested.EnsureSuccessStatusCode();
        var id = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var listPath = $"/api/admin/withdrawals?sellerId={seller}";
        var path = $"/api/admin/withdrawals/{id}";
        foreach (var url in new[] { listPath, path })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await shop.GetAsync(url)).StatusCode);
            var response = await first.GetAsync(url); response.EnsureSuccessStatusCode();
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(account, text); Assert.DoesNotContain(encrypted, text);
        }
        var list = (await first.GetFromJsonAsync<AdminWithdrawalPage>(listPath))!;
        Assert.Equal("Payout read fixture", Assert.Single(list.Items).ShopName); Assert.False(list.HasMore);
        var detail = (await second.GetFromJsonAsync<AdminWithdrawalDetail>(path))!;
        Assert.Equal("4321", detail.AccountSuffix); Assert.Equal(200000, detail.AvailableBalance); Assert.False(detail.CanAccessBank);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync(path + "/bank-details", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.PostAsync(path + "/bank-details", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path + "/bank-details", null)).StatusCode);
        (await first.PostAsJsonAsync("/api/admin/flows/approve_withdrawal", new { p_w = id })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await first.PostAsync(path + "/bank-details", null)).StatusCode);
        (await shop.PostAsJsonAsync($"/api/sellers/{seller}/flows/set_seller_bank", new { p_bank = "New Bank", p_holder = "NEW HOLDER", p_account_enc = "55555555555" })).EnsureSuccessStatusCode();
        var reveal = await second.PostAsync(path + "/bank-details", null); reveal.EnsureSuccessStatusCode();
        Assert.Equal("no-store", reveal.Headers.CacheControl!.ToString());
        var bank = (await reveal.Content.ReadFromJsonAsync<WithdrawalBankResponse>())!;
        Assert.Equal(account, bank.Account); Assert.Equal("Snapshot Bank", bank.BankName); Assert.Equal("SNAPSHOT HOLDER", bank.Holder);
        Assert.True((await second.GetFromJsonAsync<AdminWithdrawalDetail>(path))!.CanAccessBank);
        await using var audit = data.CreateCommand("SELECT count(*),coalesce(string_agg(payload::text,''),'') FROM notify.audit_logs WHERE entity_id=@id AND action='WITHDRAWAL_BANK_VIEWED' AND actor_id=@actor");
        audit.Parameters.AddWithValue("id", id); audit.Parameters.AddWithValue("actor", payer);
        await using (var reader = await audit.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0)); Assert.DoesNotContain(account, reader.GetString(1));
        }
        (await second.PostAsJsonAsync("/api/admin/flows/pay_withdrawal", new { p_w = id, p_proof_url = EvidenceFixture.Admin(factory.Services, "pay_withdrawal", id, payer) })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync(path + "/bank-details", null)).StatusCode);
        var paid = (await second.GetFromJsonAsync<AdminWithdrawalDetail>(path))!;
        Assert.Equal(payer, paid.Withdrawal.PaidBy); Assert.Equal("PAID", paid.Withdrawal.Status); Assert.Equal(100000, paid.AvailableBalance);
        Assert.False(paid.CanAccessBank);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.GetAsync(listPath + "&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await first.GetAsync(listPath + "&status=ALL")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await first.GetAsync($"/api/admin/withdrawals/{Guid.NewGuid()}")).StatusCode);
        var secondRequest = await shop.PostAsJsonAsync($"/api/sellers/{seller}/flows/request_withdrawal", new { p_amount = 50000 });
        secondRequest.EnsureSuccessStatusCode();
        var legacyId = (await secondRequest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        (await first.PostAsJsonAsync("/api/admin/flows/approve_withdrawal", new { p_w = legacyId })).EnsureSuccessStatusCode();
        await using var corrupt = data.CreateCommand("UPDATE payment.withdrawal_requests SET bank_account_no_enc='legacy-plaintext' WHERE id=@id");
        corrupt.Parameters.AddWithValue("id", legacyId); await corrupt.ExecuteNonQueryAsync();
        var legacyPath = $"/api/admin/withdrawals/{legacyId}";
        var legacy = (await second.GetFromJsonAsync<AdminWithdrawalDetail>(legacyPath))!;
        Assert.Equal("NEEDS_UPDATE", legacy.BankStatus); Assert.Null(legacy.AccountSuffix); Assert.False(legacy.CanAccessBank);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync(legacyPath + "/bank-details", null)).StatusCode);
        await using var pages = data.CreateCommand("""
            INSERT INTO payment.withdrawal_requests(seller_id,amount,bank_name,bank_account_no_enc,bank_holder,status,reject_reason)
            SELECT @seller,50000,'Pagination Bank',@encrypted,'TEST HOLDER','REJECTED','Pagination fixture' FROM generate_series(1,26);
            """);
        pages.Parameters.AddWithValue("seller", seller); pages.Parameters.AddWithValue("encrypted", encrypted); await pages.ExecuteNonQueryAsync();
        var firstPage = (await first.GetFromJsonAsync<AdminWithdrawalPage>(listPath + "&status=REJECTED"))!;
        var nextPage = (await first.GetFromJsonAsync<AdminWithdrawalPage>(listPath + "&status=REJECTED&page=2"))!;
        Assert.Equal(25, firstPage.Items.Count); Assert.True(firstPage.HasMore);
        Assert.Single(nextPage.Items); Assert.False(nextPage.HasMore); Assert.Equal(2, nextPage.Page);
        Assert.DoesNotContain(nextPage.Items[0].Id, firstPage.Items.Select(x => x.Id));
    }
}
