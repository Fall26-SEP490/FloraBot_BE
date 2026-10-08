using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.Modules.Payment;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdminRefundsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task BankAccessRequiresHumanApprovalDistinctPayerAndAuditAndStopsAfterPayment()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var order = Guid.NewGuid(); var payer = Guid.NewGuid();
        await using var fixture = data.CreateCommand("""
            INSERT INTO identity.users(id,email,password_hash,full_name,role)
            VALUES(@payer,@payer::text || '@example.invalid','!unprovisioned','Refund payer','ADMIN');
            INSERT INTO ordering.orders(id,order_code,checkout_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at)
            VALUES(@id,@id::text,@id,'20000000-0000-0000-0000-000000000001','40000000-0000-0000-0000-000000000001','COMPLETED',350000,350000,flow.new_tracking_token(),public.app_now());
            INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status,paid_at)
            VALUES('CHARGE','ORDER_CHECKOUT',@id,'MANUAL',@id::text,350000,'SUCCEEDED',public.app_now());
            SELECT flow.create_refund(@id,100000,'Synthetic automatic refund',(SELECT id FROM identity.users WHERE role='SYSTEM' LIMIT 1));
            """);
        fixture.Parameters.AddWithValue("id", order); fixture.Parameters.AddWithValue("payer", payer);
        var refund = (Guid)(await fixture.ExecuteScalarAsync())!;
        using var admin = factory.CreateClient(); admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        using var second = factory.CreateClient(); second.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: payer));
        using var seller = factory.CreateClient(); seller.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token());
        using var anonymous = factory.CreateClient();
        var path = $"/api/admin/refunds/{refund}";
        var listPath = $"/api/admin/refunds?orderId={order}";
        foreach (var url in new[] { path, listPath })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await seller.GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(path + "/bank-details", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await seller.PostAsync(path + "/bank-details", null)).StatusCode);
        var initial = (await admin.GetFromJsonAsync<AdminRefundDetail>(path))!;
        Assert.True(initial.CanApprove); Assert.True(initial.Refund.RequiresApproval);
        Assert.Equal("NEEDS_UPDATE", initial.BankStatus); Assert.False(initial.CanAccessBank);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync(path + "/bank-details", null)).StatusCode);
        (await admin.PostAsJsonAsync("/api/admin/flows/approve_refund", new { p_refund = refund })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(path + "/bank-details", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync(path + "/bank-details", null)).StatusCode);
        await using var tokenQuery = data.CreateCommand("SELECT tracking_token FROM ordering.orders WHERE id=@id");
        tokenQuery.Parameters.AddWithValue("id", order); var token = (string)(await tokenQuery.ExecuteScalarAsync())!;
        const string account = "001234567890";
        (await anonymous.PostAsJsonAsync("/api/receipts/flows/submit_refund_info", new { p_order = order, p_tracking = token, p_bank = "ACB", p_holder = "Nguyen Van A", p_account_enc = account })).EnsureSuccessStatusCode();
        foreach (var url in new[] { path, listPath })
        {
            var response = await admin.GetAsync(url); response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl?.NoStore);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(account, json); Assert.DoesNotContain("NGUYEN VAN A", json); Assert.DoesNotContain(token, json);
        }
        Assert.False((await admin.GetFromJsonAsync<AdminRefundDetail>(path))!.CanAccessBank);
        var ready = (await second.GetFromJsonAsync<AdminRefundDetail>(path))!;
        Assert.True(ready.CanAccessBank); Assert.False(ready.CanApprove); Assert.Equal("READY", ready.BankStatus);
        var reveal = await second.PostAsync(path + "/bank-details", null); reveal.EnsureSuccessStatusCode();
        Assert.True(reveal.Headers.CacheControl?.NoStore);
        var bank = (await reveal.Content.ReadFromJsonAsync<RefundBankResponse>())!;
        Assert.Equal(account, bank.Account); Assert.Equal("ACB", bank.BankName); Assert.Equal("NGUYEN VAN A", bank.Holder);
        await using var audit = data.CreateCommand("SELECT count(*),coalesce(string_agg(payload::text,''),'') FROM notify.audit_logs WHERE action='REFUND_BANK_VIEWED' AND entity_id=@id AND actor_id=@payer");
        audit.Parameters.AddWithValue("id", refund); audit.Parameters.AddWithValue("payer", payer);
        await using (var reader = await audit.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync()); Assert.Equal(1L, reader.GetInt64(0));
            Assert.DoesNotContain(account, reader.GetString(1)); Assert.DoesNotContain(bank.Holder, reader.GetString(1));
        }
        await using var corrupt = data.CreateCommand("UPDATE ordering.orders SET refund_bank_account_enc='legacy-plaintext' WHERE id=@id");
        corrupt.Parameters.AddWithValue("id", order); await corrupt.ExecuteNonQueryAsync();
        Assert.Equal("NEEDS_UPDATE", (await second.GetFromJsonAsync<AdminRefundDetail>(path))!.BankStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync(path + "/bank-details", null)).StatusCode);
        (await anonymous.PostAsJsonAsync("/api/receipts/flows/submit_refund_info", new { p_order = order, p_tracking = token, p_bank = "ACB", p_holder = "Nguyen Van A", p_account_enc = account })).EnsureSuccessStatusCode();
        var more = await admin.PostAsJsonAsync("/api/admin/flows/admin_refund", new { p_order = order, p_amount = 50000, p_reason = "Second synthetic refund" });
        more.EnsureSuccessStatusCode();
        var nextId = (await more.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        (await second.PostAsJsonAsync("/api/admin/flows/confirm_refund", new { p_refund = refund, p_proof_url = EvidenceFixture.Admin(factory.Services, "confirm_refund", refund, payer) })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsync(path + "/bank-details", null)).StatusCode);
        var paid = (await second.GetFromJsonAsync<AdminRefundDetail>(path))!;
        Assert.Equal("SUCCEEDED", paid.Refund.Status); Assert.Equal(payer, paid.Refund.PaidBy);
        Assert.Equal("NOT_APPLICABLE", paid.BankStatus); Assert.False(paid.CanAccessBank);
        var nextPath = $"/api/admin/refunds/{nextId}";
        Assert.True((await second.GetFromJsonAsync<AdminRefundDetail>(nextPath))!.CanAccessBank);
        (await second.PostAsJsonAsync("/api/admin/flows/confirm_refund", new { p_refund = nextId, p_proof_url = EvidenceFixture.Admin(factory.Services, "confirm_refund", nextId, payer) })).EnsureSuccessStatusCode();
        await using var cleared = data.CreateCommand("SELECT refund_bank_name IS NULL AND refund_bank_holder IS NULL AND refund_bank_account_enc IS NULL FROM ordering.orders WHERE id=@id");
        cleared.Parameters.AddWithValue("id", order); Assert.Equal(true, await cleared.ExecuteScalarAsync());
        Assert.Equal(2, (await admin.GetFromJsonAsync<AdminRefundPage>(listPath + "&status=SUCCEEDED"))!.Items.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(listPath + "&page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(listPath + "&status=ALL")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/admin/refunds/{Guid.NewGuid()}")).StatusCode);
        await using var checkAudit = data.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='REFUND_BANK_VIEWED' AND entity_id=@id");
        checkAudit.Parameters.AddWithValue("id", refund); Assert.Equal(1L, await checkAudit.ExecuteScalarAsync());
        await using var history = data.CreateCommand("""
            INSERT INTO payment.payments(kind,purpose,checkout_id,order_id,parent_payment_id,gateway,idempotency_key,amount,status,approved_by)
            SELECT 'REFUND','ORDER_CHECKOUT',@order,@order,p.id,'MANUAL',public.uuid_v7()::text,1,'CANCELLED',p_admin.id
            FROM payment.payments p CROSS JOIN generate_series(1,26) n
            CROSS JOIN (SELECT id FROM identity.users WHERE id='10000000-0000-0000-0000-000000000001') p_admin
            WHERE p.checkout_id=@order AND p.kind='CHARGE';
            """);
        history.Parameters.AddWithValue("order", order); await history.ExecuteNonQueryAsync();
        var firstPage = (await admin.GetFromJsonAsync<AdminRefundPage>(listPath + "&status=CANCELLED"))!;
        var secondPage = (await admin.GetFromJsonAsync<AdminRefundPage>(listPath + "&status=CANCELLED&page=2"))!;
        Assert.Equal(25, firstPage.Items.Count); Assert.True(firstPage.HasMore);
        Assert.Single(secondPage.Items); Assert.False(secondPage.HasMore);
        Assert.DoesNotContain(secondPage.Items[0].Id, firstPage.Items.Select(x => x.Id));
    }
}
