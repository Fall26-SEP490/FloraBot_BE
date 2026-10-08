using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class PayoutApprovalTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task WithdrawalRequiresApprovalAndDistinctPayerWithoutDuplicateLedger()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var (seller, owner, admin, payer) = await Setup(data);
        using var shop = factory.CreateClient(); shop.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: owner, sellerId: seller));
        using var first = factory.CreateClient(); first.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: admin));
        using var second = factory.CreateClient(); second.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: payer));
        var requested = await shop.PostAsJsonAsync($"/api/sellers/{seller}/flows/request_withdrawal", new { p_amount = 100000 });
        requested.EnsureSuccessStatusCode();
        var id = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var payment = new { p_w = id, p_proof_url = EvidenceFixture.Admin(factory.Services, "pay_withdrawal", id, payer) };
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/admin/flows/pay_withdrawal", payment)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await shop.PostAsJsonAsync("/api/admin/flows/approve_withdrawal", new { p_w = id })).StatusCode);
        (await first.PostAsJsonAsync("/api/admin/flows/approve_withdrawal", new { p_w = id })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/admin/flows/approve_withdrawal", new { p_w = id })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await first.PostAsJsonAsync("/api/admin/flows/pay_withdrawal", new { p_w = id, p_proof_url = EvidenceFixture.Admin(factory.Services, "pay_withdrawal", id, admin) })).StatusCode);
        await using var before = data.CreateCommand("SELECT count(*) FROM payment.ledger_entries WHERE ref_type='WITHDRAWAL' AND ref_id=@id");
        before.Parameters.AddWithValue("id", id); Assert.Equal(0L, await before.ExecuteScalarAsync());
        var payouts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => second.PostAsJsonAsync("/api/admin/flows/pay_withdrawal", payment)));
        Assert.Single(payouts, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(payouts, result => result.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/admin/flows/pay_withdrawal", payment)).StatusCode);
        Assert.Equal(2L, await before.ExecuteScalarAsync());
        await using var check = data.CreateCommand("SELECT status,approved_by,paid_by,(SELECT sum(amount)::bigint FROM payment.ledger_entries WHERE ref_id=@id),(SELECT count(*) FROM notify.audit_logs WHERE entity_id=@id AND action='WITHDRAWAL_APPROVED' AND actor_id=@admin) FROM payment.withdrawal_requests WHERE id=@id");
        check.Parameters.AddWithValue("id", id); check.Parameters.AddWithValue("admin", admin);
        await using var reader = await check.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal("PAID", reader.GetString(0)); Assert.Equal(admin, reader.GetGuid(1)); Assert.Equal(payer, reader.GetGuid(2)); Assert.Equal(0L, reader.GetInt64(3)); Assert.Equal(1L, reader.GetInt64(4));
    }

    [Fact]
    public async Task AutomaticRefundNeedsHumanApprovalAndDifferentPayer()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var (seller, _, admin, payer) = await Setup(data);
        var accessory = Guid.NewGuid();
        await using var stock = data.CreateCommand("INSERT INTO kiosk_ops.accessories(id,seller_id,kiosk_id,name,price,stock_quantity) VALUES(@id,@seller,'40000000-0000-0000-0000-000000000001',@name,100000,1)");
        stock.Parameters.AddWithValue("id", accessory); stock.Parameters.AddWithValue("seller", seller); stock.Parameters.AddWithValue("name", accessory.ToString()); await stock.ExecuteNonQueryAsync();
        await using var checkout = data.CreateCommand("SELECT flow.kiosk_checkout('40000000-0000-0000-0000-000000000001',NULL,NULL,jsonb_build_array(jsonb_build_object('id',@id::text,'qty',1)))");
        checkout.Parameters.AddWithValue("id", accessory);
        var checkoutId = (Guid)(await checkout.ExecuteScalarAsync())!;
        await using var paid = data.CreateCommand("SELECT flow.checkout_paid(@id,@txn,true,100000)"); paid.Parameters.AddWithValue("id", checkoutId); paid.Parameters.AddWithValue("txn", "approval-test-" + checkoutId); await paid.ExecuteNonQueryAsync();
        await using var refund = data.CreateCommand("SELECT flow.create_refund(id,50000,'Test automatic refund',flow.sys_user()) FROM ordering.orders WHERE checkout_id=@id"); refund.Parameters.AddWithValue("id", checkoutId);
        var refundId = (Guid)(await refund.ExecuteScalarAsync())!;
        await using var bank = data.CreateCommand("SELECT flow.submit_refund_info(id,tracking_token,'Test Bank','encrypted-test','Test Customer') FROM ordering.orders WHERE checkout_id=@id"); bank.Parameters.AddWithValue("id", checkoutId); await bank.ExecuteNonQueryAsync();
        using var first = factory.CreateClient(); first.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: admin));
        using var second = factory.CreateClient(); second.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN", userId: payer));
        var payment = new { p_refund = refundId, p_proof_url = EvidenceFixture.Admin(factory.Services, "confirm_refund", refundId, payer) };
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/admin/flows/confirm_refund", payment)).StatusCode);
        (await first.PostAsJsonAsync("/api/admin/flows/approve_refund", new { p_refund = refundId })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/admin/flows/approve_refund", new { p_refund = refundId })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await first.PostAsJsonAsync("/api/admin/flows/confirm_refund", new { p_refund = refundId, p_proof_url = EvidenceFixture.Admin(factory.Services, "confirm_refund", refundId, admin) })).StatusCode);
        (await second.PostAsJsonAsync("/api/admin/flows/confirm_refund", payment)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await second.PostAsJsonAsync("/api/admin/flows/confirm_refund", payment)).StatusCode);
        await using var check = data.CreateCommand("SELECT p.status,p.approved_by,p.paid_by,o.refund_bank_account_enc IS NULL,(SELECT sum(amount)::bigint FROM payment.ledger_entries) FROM payment.payments p JOIN ordering.orders o ON o.id=p.order_id WHERE p.id=@id");
        check.Parameters.AddWithValue("id", refundId);
        await using var reader = await check.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync());
        Assert.Equal("SUCCEEDED", reader.GetString(0)); Assert.Equal(admin, reader.GetGuid(1)); Assert.Equal(payer, reader.GetGuid(2)); Assert.True(reader.GetBoolean(3)); Assert.Equal(0L, reader.GetInt64(4));
        await reader.DisposeAsync();
        await using var orderQuery = data.CreateCommand("SELECT id FROM ordering.orders WHERE checkout_id=@id");
        orderQuery.Parameters.AddWithValue("id", checkoutId);
        var orderId = (Guid)(await orderQuery.ExecuteScalarAsync())!;
        var manual = await first.PostAsJsonAsync("/api/admin/flows/admin_refund", new { p_order = orderId, p_amount = 10000, p_reason = "Test manual correction" });
        manual.EnsureSuccessStatusCode();
        var manualId = (await manual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        await using var outbox = data.CreateCommand("""
            SELECT "Content"::jsonb->'Value' FROM cap.published
            WHERE "Name"=@name AND "Content"::jsonb->'Value'->>'refund_payment_id'=@id
            """);
        outbox.Parameters.AddWithValue("name", FloraBot.Api.Modules.Payment.RefundEvents.Approved);
        outbox.Parameters.AddWithValue("id", manualId.ToString());
        await using var eventReader = await outbox.ExecuteReaderAsync();
        Assert.True(await eventReader.ReadAsync());
        var message = JsonSerializer.Deserialize<JsonElement>(eventReader.GetString(0));
        Assert.Equal(orderId, message.GetProperty("order_id").GetGuid());
        Assert.Equal(10000, message.GetProperty("amount").GetInt64());
        Assert.False(await eventReader.ReadAsync());
    }

    internal static async Task<(Guid Seller, Guid Owner, Guid Admin, Guid Payer)> Setup(NpgsqlDataSource data)
    {
        var seller = Guid.NewGuid(); var owner = Guid.NewGuid(); var admin = Guid.NewGuid(); var payer = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at,bank_name,bank_account_no_enc,bank_holder)
            VALUES(@seller,'Payout test','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30,'Test Bank','encrypted-test','Test shop');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id) VALUES(@owner,@owner::text || '@example.invalid','!unprovisioned','Owner','SELLER',@seller);
            INSERT INTO identity.users(id,email,password_hash,full_name,role) VALUES(@admin,@admin::text || '@example.invalid','!unprovisioned','Approver','ADMIN'),(@payer,@payer::text || '@example.invalid','!unprovisioned','Payer','ADMIN');
            INSERT INTO payment.ledger_entries(journal_id,account,seller_id,amount,ref_type,ref_id,memo)
            VALUES(@seller,'GATEWAY_CLEARING',NULL,200000,'ORDER',@seller,'Isolated test funding'),(@seller,'SELLER_AVAILABLE',@seller,-200000,'ORDER',@seller,'Isolated test funding');
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("owner", owner); setup.Parameters.AddWithValue("admin", admin); setup.Parameters.AddWithValue("payer", payer);
        await setup.ExecuteNonQueryAsync();
        return (seller, owner, admin, payer);
    }
}
