using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SeasonalSalesIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task TenantIsolationAndAuthorization_401_403_404_AndDisabledActor()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var user2 = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
                VALUES (@seller1, 'Shop One', '0901111111', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30),
                       (@seller2, 'Shop Two', '0902222222', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);
                INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
                VALUES (@user1, @user1::text || '@test.local', '!unprovisioned', 'Seller 1', 'SELLER', @seller1, 'ACTIVE'),
                       (@user2, @user2::text || '@test.local', '!unprovisioned', 'Seller 2', 'SELLER', @seller2, 'ACTIVE');
                """;
            cmd.Parameters.AddWithValue("seller1", seller1);
            cmd.Parameters.AddWithValue("seller2", seller2);
            cmd.Parameters.AddWithValue("user1", user1);
            cmd.Parameters.AddWithValue("user2", user2);
            await cmd.ExecuteNonQueryAsync();
        }

        using var anonClient = factory.CreateClient();
        var unauthResp = await anonClient.GetAsync($"/api/sellers/{seller1}/ai/seasonal-sales");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // Cross-tenant: user2 attempts to read seller1's data -> 404 (SameSeller protection hides other tenants)
        using var client2 = factory.CreateClient();
        client2.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user2, sellerId: seller2));
        var crossResp = await client2.GetAsync($"/api/sellers/{seller1}/ai/seasonal-sales");
        Assert.Equal(HttpStatusCode.NotFound, crossResp.StatusCode);

        // Non-existent seller
        var missingResp = await client2.GetAsync($"/api/sellers/{Guid.NewGuid()}/ai/seasonal-sales");
        Assert.Equal(HttpStatusCode.NotFound, missingResp.StatusCode);

        // Deactivated actor in DB triggers JwtBearer OnTokenValidated DB check -> 401 Unauthorized
        await using (var disableCmd = conn.CreateCommand())
        {
            disableCmd.CommandText = "UPDATE identity.users SET status = 'DISABLED' WHERE id = @user1";
            disableCmd.Parameters.AddWithValue("user1", user1);
            await disableCmd.ExecuteNonQueryAsync();
        }

        using var client1 = factory.CreateClient();
        client1.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user1, sellerId: seller1));
        var disabledResp = await client1.GetAsync($"/api/sellers/{seller1}/ai/seasonal-sales");
        Assert.Equal(HttpStatusCode.Unauthorized, disabledResp.StatusCode);
    }

    [Fact]
    public async Task SeasonalSales_Aggregation_RefundIsolation_MultiProductDistinctInvoice()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();
        var user1 = Guid.NewGuid();
        var kiosk1 = Guid.NewGuid();
        var kiosk2 = Guid.NewGuid();
        var slot1a = Guid.NewGuid();
        var slot1b = Guid.NewGuid();
        var slot1c = Guid.NewGuid();
        var slot1d = Guid.NewGuid();
        var slot2 = Guid.NewGuid();

        var prod1 = Guid.NewGuid();
        var prod3 = Guid.NewGuid();
        var prod2 = Guid.NewGuid();

        var bouquet1a = Guid.NewGuid();
        var bouquet1b = Guid.NewGuid();
        var bouquet1c = Guid.NewGuid();
        var bouquet1d = Guid.NewGuid();
        var bouquet2 = Guid.NewGuid();

        var order1 = Guid.NewGuid();
        var order2Multi = Guid.NewGuid();
        var orderRefundedOther = Guid.NewGuid();
        var orderRefundedSelf = Guid.NewGuid();
        var orderUnpaid = Guid.NewGuid();
        var orderCancelled = Guid.NewGuid();
        var orderSeller2 = Guid.NewGuid();

        var chk1 = Guid.NewGuid();
        var chkMulti = Guid.NewGuid();
        var chkRefundTest = Guid.NewGuid();
        var chkSeller2 = Guid.NewGuid();

        var photo1Url = "https://res.cloudinary.com/florabot/image/upload/v1/florabot/p1.jpg";
        var photo2Url = "https://res.cloudinary.com/florabot/image/upload/v1/florabot/p2.jpg";
        var validSha = new string('a', 64);

        await using var conn = await data.OpenConnectionAsync();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
                VALUES (@seller1, 'Shop One', '0901111111', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30),
                       (@seller2, 'Shop Two', '0902222222', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);
                INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
                VALUES (@user1, @user1::text || '@test.local', '!unprovisioned', 'Seller 1', 'SELLER', @seller1, 'ACTIVE');

                -- Products: prod1 and prod3 under seller1, prod2 under seller2
                INSERT INTO catalog.flower_products(id, seller_id, name, price, tags, shelf_life_hours, status, created_at)
                VALUES (@prod1, @seller1, 'Bó Hoa Hồng Đỏ', 300000, ARRAY['ROSE'], 48, 'ACTIVE', '2026-01-01 00:00:00+07'),
                       (@prod3, @seller1, 'Bó Hoa Baby Trắng', 200000, ARRAY['BABY'], 48, 'ACTIVE', '2026-01-01 00:00:00+07'),
                       (@prod2, @seller2, 'Bó Hoa Hồng Đỏ', 400000, ARRAY['PREMIUM'], 48, 'ACTIVE', '2026-01-01 00:00:00+07');

                -- Attachments with valid HTTPS and exact 64-hex SHA-256
                INSERT INTO notify.attachments(id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase)
                VALUES (gen_random_uuid(), 'catalog', 'flower_product', @prod1, @photo1Url, 'image/jpeg', 2048, @validSha, 'PRODUCT'),
                       (gen_random_uuid(), 'catalog', 'flower_product', @prod2, @photo2Url, 'image/jpeg', 4096, @validSha, 'PRODUCT');

                -- Separate Kiosks and Slots for each seller
                INSERT INTO kiosk_ops.kiosks(id, seller_id, code, name, address, region, hardware_id, mqtt_client_id, status)
                VALUES (@kiosk1, @seller1, @kiosk1::text, 'Kiosk 1', 'Address 1', 'HCM', @kiosk1::text, @kiosk1::text, 'ONLINE'),
                       (@kiosk2, @seller2, @kiosk2::text, 'Kiosk 2', 'Address 2', 'HCM', @kiosk2::text, @kiosk2::text, 'ONLINE');

                INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel)
                VALUES (@slot1a, @kiosk1, 'A01', 0),
                       (@slot1b, @kiosk1, 'A02', 1),
                       (@slot1c, @kiosk1, 'A03', 2),
                       (@slot1d, @kiosk1, 'A04', 3),
                       (@slot2,  @kiosk2, 'B01', 0);

                -- Bouquets
                INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
                VALUES (@b1a, @prod1, @seller1, @b1a::text, 300000, 'SOLD', now() + interval '1 day', now()),
                       (@b1b, @prod1, @seller1, @b1b::text, 300000, 'SOLD', now() + interval '1 day', now()),
                       (@b1c, @prod3, @seller1, @b1c::text, 200000, 'SOLD', now() + interval '1 day', now()),
                       (@b1d, @prod1, @seller1, @b1d::text, 300000, 'SOLD', now() + interval '1 day', now()),
                       (@b2,  @prod2, @seller2, @b2::text,  400000, 'SOLD', now() + interval '1 day', now());

                -- 1. Order 1: Completed single-product order on 2026-10-20
                INSERT INTO ordering.orders(id, order_code, checkout_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, tracking_token, completed_at)
                VALUES (@o1, @o1::text, @chk1, @seller1, @kiosk1, 'COMPLETED', 300000, 0, 300000, @o1::text, '2026-10-20 09:00:00+07');
                INSERT INTO ordering.order_items(id, order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status)
                VALUES (gen_random_uuid(), @o1, 'BOUQUET', @b1a, @slot1a, 'Bó Hoa Hồng Đỏ', 1, 300000, 300000, 'ACTIVE');
                INSERT INTO payment.payments(id, kind, purpose, checkout_id, order_id, gateway, idempotency_key, amount, status, paid_at)
                VALUES (gen_random_uuid(), 'CHARGE', 'ORDER_CHECKOUT', @chk1, @o1, 'PAYOS', gen_random_uuid()::text, 300000, 'SUCCEEDED', '2026-10-20 08:59:00+07');

                -- 2. Order 2: Multi-product completed order on 2026-10-20 (prod1 + prod3 in the SAME order)
                INSERT INTO ordering.orders(id, order_code, checkout_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, tracking_token, completed_at)
                VALUES (@o2, @o2::text, @chkMulti, @seller1, @kiosk1, 'COMPLETED', 500000, 0, 500000, @o2::text, '2026-10-20 14:00:00+07');
                INSERT INTO ordering.order_items(id, order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status)
                VALUES (gen_random_uuid(), @o2, 'BOUQUET', @b1b, @slot1b, 'Bó Hoa Hồng Đỏ', 1, 300000, 300000, 'ACTIVE'),
                       (gen_random_uuid(), @o2, 'BOUQUET', @b1c, @slot1c, 'Bó Hoa Baby Trắng', 1, 200000, 200000, 'ACTIVE');
                INSERT INTO payment.payments(id, kind, purpose, checkout_id, order_id, gateway, idempotency_key, amount, status, paid_at)
                VALUES (gen_random_uuid(), 'CHARGE', 'ORDER_CHECKOUT', @chkMulti, @o2, 'PAYOS', gen_random_uuid()::text, 500000, 'SUCCEEDED', '2026-10-20 13:58:00+07');

                -- 3. Checkout with two orders: OrderSelf (refunded) and OrderOther (excluded because self was refunded)
                INSERT INTO ordering.orders(id, order_code, checkout_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, tracking_token, completed_at)
                VALUES (@oRefundSelf, @oRefundSelf::text, @chkRefundTest, @seller1, @kiosk1, 'COMPLETED', 300000, 0, 300000, @oRefundSelf::text, '2026-10-20 15:00:00+07');
                INSERT INTO ordering.order_items(id, order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status)
                VALUES (gen_random_uuid(), @oRefundSelf, 'BOUQUET', @b1d, @slot1d, 'Bó Hoa Hồng Đỏ', 1, 300000, 300000, 'ACTIVE');
                INSERT INTO payment.payments(id, kind, purpose, checkout_id, order_id, gateway, idempotency_key, amount, status, paid_at)
                VALUES (gen_random_uuid(), 'CHARGE', 'ORDER_CHECKOUT', @chkRefundTest, @oRefundSelf, 'PAYOS', gen_random_uuid()::text, 300000, 'SUCCEEDED', '2026-10-20 14:59:00+07');
                -- Succeeded refund specific to orderRefundSelf
                INSERT INTO payment.payments(id, kind, purpose, checkout_id, order_id, gateway, idempotency_key, amount, status, paid_at, approved_by, parent_payment_id)
                VALUES (gen_random_uuid(), 'REFUND', 'ORDER_CHECKOUT', @chkRefundTest, @oRefundSelf, 'MANUAL', gen_random_uuid()::text, 300000, 'SUCCEEDED', '2026-10-20 16:00:00+07', @user1, (SELECT id FROM payment.payments WHERE checkout_id = @chkRefundTest AND kind = 'CHARGE' LIMIT 1));

                -- 4. Cancelled and Unpaid orders
                INSERT INTO ordering.orders(id, order_code, checkout_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, tracking_token)
                VALUES (@oUnpaid, @oUnpaid::text, gen_random_uuid(), @seller1, @kiosk1, 'AWAITING_PAYMENT', 300000, 0, 300000, @oUnpaid::text),
                       (@oCancelled, @oCancelled::text, gen_random_uuid(), @seller1, @kiosk1, 'CANCELLED', 300000, 0, 300000, @oCancelled::text);

                -- 5. Order under seller 2
                INSERT INTO ordering.orders(id, order_code, checkout_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, tracking_token, completed_at)
                VALUES (@oSeller2, @oSeller2::text, @chkSeller2, @seller2, @kiosk2, 'COMPLETED', 400000, 0, 400000, @oSeller2::text, '2026-10-20 10:00:00+07');
                INSERT INTO ordering.order_items(id, order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status)
                VALUES (gen_random_uuid(), @oSeller2, 'BOUQUET', @b2, @slot2, 'Bó Hoa Hồng Đỏ', 1, 400000, 400000, 'ACTIVE');
                INSERT INTO payment.payments(id, kind, purpose, checkout_id, order_id, gateway, idempotency_key, amount, status, paid_at)
                VALUES (gen_random_uuid(), 'CHARGE', 'ORDER_CHECKOUT', @chkSeller2, @oSeller2, 'PAYOS', gen_random_uuid()::text, 400000, 'SUCCEEDED', '2026-10-20 09:59:00+07');
                """;

            setup.Parameters.AddWithValue("seller1", seller1);
            setup.Parameters.AddWithValue("seller2", seller2);
            setup.Parameters.AddWithValue("user1", user1);
            setup.Parameters.AddWithValue("prod1", prod1);
            setup.Parameters.AddWithValue("prod3", prod3);
            setup.Parameters.AddWithValue("prod2", prod2);
            setup.Parameters.AddWithValue("photo1Url", photo1Url);
            setup.Parameters.AddWithValue("photo2Url", photo2Url);
            setup.Parameters.AddWithValue("validSha", validSha);
            setup.Parameters.AddWithValue("kiosk1", kiosk1);
            setup.Parameters.AddWithValue("kiosk2", kiosk2);
            setup.Parameters.AddWithValue("slot1a", slot1a);
            setup.Parameters.AddWithValue("slot1b", slot1b);
            setup.Parameters.AddWithValue("slot1c", slot1c);
            setup.Parameters.AddWithValue("slot1d", slot1d);
            setup.Parameters.AddWithValue("slot2", slot2);
            setup.Parameters.AddWithValue("b1a", bouquet1a);
            setup.Parameters.AddWithValue("b1b", bouquet1b);
            setup.Parameters.AddWithValue("b1c", bouquet1c);
            setup.Parameters.AddWithValue("b1d", bouquet1d);
            setup.Parameters.AddWithValue("b2", bouquet2);
            setup.Parameters.AddWithValue("o1", order1);
            setup.Parameters.AddWithValue("o2", order2Multi);
            setup.Parameters.AddWithValue("oRefundSelf", orderRefundedSelf);
            setup.Parameters.AddWithValue("oUnpaid", orderUnpaid);
            setup.Parameters.AddWithValue("oCancelled", orderCancelled);
            setup.Parameters.AddWithValue("oSeller2", orderSeller2);
            setup.Parameters.AddWithValue("chk1", chk1);
            setup.Parameters.AddWithValue("chkMulti", chkMulti);
            setup.Parameters.AddWithValue("chkRefundTest", chkRefundTest);
            setup.Parameters.AddWithValue("chkSeller2", chkSeller2);

            await setup.ExecuteNonQueryAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user1, sellerId: seller1));

        var response = await client.GetAsync($"/api/sellers/{seller1}/ai/seasonal-sales?year=2026&holiday=VN_WOMENS_DAY");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dataResponse = await response.Content.ReadFromJsonAsync<SeasonalSalesResponse>();
        Assert.NotNull(dataResponse);

        // Crucial distinct invoice check:
        // Order 1 has prod1. Order 2 has prod1 AND prod3.
        // Total distinct invoices for the seller MUST BE 2 (Order 1 and Order 2), NOT 3!
        Assert.Equal(2, dataResponse!.TotalInvoices);
        Assert.Equal(3, dataResponse.TotalUnits); // 2 of prod1 + 1 of prod3

        var itemProd1 = dataResponse.Products.Single(p => p.ProductId == prod1);
        Assert.Equal(2, itemProd1.TotalInvoices);
        Assert.Equal(2, itemProd1.TotalUnits);
        Assert.Single(itemProd1.Photos);
        Assert.Equal(photo1Url, itemProd1.Photos[0]);

        var itemProd3 = dataResponse.Products.Single(p => p.ProductId == prod3);
        Assert.Equal(1, itemProd3.TotalInvoices);
        Assert.Equal(1, itemProd3.TotalUnits);
    }

    [Fact]
    public async Task SalesCsv_ExportsFormulaSafeAndZeroPii()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var prod = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var bouquet = Guid.NewGuid();
        var order = Guid.NewGuid();
        var chk = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
                VALUES (@seller, 'CSV Shop', '0903333333', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);
                INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
                VALUES (@user, @user::text || '@test.local', '!unprovisioned', 'CSV User', 'SELLER', @seller, 'ACTIVE');

                -- Product with formula trigger in name
                INSERT INTO catalog.flower_products(id, seller_id, name, price, tags, shelf_life_hours, status, created_at)
                VALUES (@prod, @seller, '=Hoa Hồng Khuyến Mãi', 250000, ARRAY['ROSE'], 48, 'ACTIVE', '2026-01-01 00:00:00+07');

                INSERT INTO kiosk_ops.kiosks(id, seller_id, code, name, address, region, hardware_id, mqtt_client_id, status)
                VALUES (@kiosk, @seller, @kiosk::text, 'CSV Kiosk', 'Address', 'HCM', @kiosk::text, @kiosk::text, 'ONLINE');

                INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel)
                VALUES (@slot, @kiosk, 'A01', 0);

                INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
                VALUES (@b, @prod, @seller, @b::text, 250000, 'SOLD', now() + interval '1 day', now());

                INSERT INTO ordering.orders(id, order_code, checkout_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, tracking_token, completed_at)
                VALUES (@o, @o::text, @chk, @seller, @kiosk, 'COMPLETED', 250000, 0, 250000, @o::text, '2026-10-20 10:00:00+07');
                INSERT INTO ordering.order_items(id, order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status)
                VALUES (gen_random_uuid(), @o, 'BOUQUET', @b, @slot, '=Hoa Hồng Khuyến Mãi', 1, 250000, 250000, 'ACTIVE');
                INSERT INTO payment.payments(id, kind, purpose, checkout_id, order_id, gateway, idempotency_key, amount, status, paid_at)
                VALUES (gen_random_uuid(), 'CHARGE', 'ORDER_CHECKOUT', @chk, @o, 'PAYOS', gen_random_uuid()::text, 250000, 'SUCCEEDED', '2026-10-20 09:50:00+07');
                """;
            cmd.Parameters.AddWithValue("seller", seller);
            cmd.Parameters.AddWithValue("user", user);
            cmd.Parameters.AddWithValue("prod", prod);
            cmd.Parameters.AddWithValue("kiosk", kiosk);
            cmd.Parameters.AddWithValue("slot", slot);
            cmd.Parameters.AddWithValue("b", bouquet);
            cmd.Parameters.AddWithValue("o", order);
            cmd.Parameters.AddWithValue("chk", chk);
            await cmd.ExecuteNonQueryAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user, sellerId: seller));

        var response = await client.GetAsync($"/api/sellers/{seller}/ai/sales.csv?from=2026-10-01&to=2026-10-31");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);

        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("invoice_id,product_id,product_name,paid_date,quantity,season,holiday", csv);
        Assert.Contains("\"'=Hoa Hồng Khuyến Mãi\"", csv); // Formula trigger prepended with '
        Assert.Contains("2026-10-20", csv);
        Assert.Contains("VN_WOMENS_DAY", csv);

        // Zero PII check
        Assert.DoesNotContain("customer", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bank", csv, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("year=0")]
    [InlineData("year=-2026")]
    [InlineData("year=99999")]
    [InlineData("month=0")]
    [InlineData("month=13")]
    [InlineData("season=INVALID_SEASON")]
    [InlineData("holiday=INVALID_HOLIDAY")]
    [InlineData("month=1&season=SUMMER")]
    [InlineData("month=1&holiday=VALENTINE")]
    [InlineData("from=2026-11-01&to=2026-10-01")]
    public async Task InputValidation_RejectsMalformedOrConflictingFiltersWith400(string query)
    {
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();

        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var conn = await data.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
                VALUES (@seller, 'Val Shop', '0905555555', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);
                INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
                VALUES (@user, @user::text || '@test.local', '!unprovisioned', 'Val User', 'SELLER', @seller, 'ACTIVE');
                """;
            cmd.Parameters.AddWithValue("seller", seller);
            cmd.Parameters.AddWithValue("user", user);
            await cmd.ExecuteNonQueryAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user, sellerId: seller));

        var resp = await client.GetAsync($"/api/sellers/{seller}/ai/seasonal-sales?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task WinterCrossYearAndDec25Overlap_PreservesBothHolidays()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var prod = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var bouquet = Guid.NewGuid();
        var order = Guid.NewGuid();
        var chk = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
                VALUES (@seller, 'Dec25 Shop', '0906666666', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);
                INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
                VALUES (@user, @user::text || '@test.local', '!unprovisioned', 'Dec25 User', 'SELLER', @seller, 'ACTIVE');

                INSERT INTO catalog.flower_products(id, seller_id, name, price, tags, shelf_life_hours, status, created_at)
                VALUES (@prod, @seller, 'Hoa Giáng Sinh', 300000, ARRAY['XMAS'], 48, 'ACTIVE', '2026-01-01 00:00:00+07');

                INSERT INTO kiosk_ops.kiosks(id, seller_id, code, name, address, region, hardware_id, mqtt_client_id, status)
                VALUES (@kiosk, @seller, @kiosk::text, 'Xmas Kiosk', 'Address', 'HCM', @kiosk::text, @kiosk::text, 'ONLINE');

                INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel)
                VALUES (@slot, @kiosk, 'A01', 0);

                INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
                VALUES (@b, @prod, @seller, @b::text, 300000, 'SOLD', now() + interval '1 day', now());

                -- Sale completed on Dec 25
                INSERT INTO ordering.orders(id, order_code, checkout_id, seller_id, kiosk_id, status, subtotal, discount_amount, total_amount, tracking_token, completed_at)
                VALUES (@o, @o::text, @chk, @seller, @kiosk, 'COMPLETED', 300000, 0, 300000, @o::text, '2026-12-25 10:00:00+07');
                INSERT INTO ordering.order_items(id, order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status)
                VALUES (gen_random_uuid(), @o, 'BOUQUET', @b, @slot, 'Hoa Giáng Sinh', 1, 300000, 300000, 'ACTIVE');
                INSERT INTO payment.payments(id, kind, purpose, checkout_id, order_id, gateway, idempotency_key, amount, status, paid_at)
                VALUES (gen_random_uuid(), 'CHARGE', 'ORDER_CHECKOUT', @chk, @o, 'PAYOS', gen_random_uuid()::text, 300000, 'SUCCEEDED', '2026-12-25 09:50:00+07');
                """;
            cmd.Parameters.AddWithValue("seller", seller);
            cmd.Parameters.AddWithValue("user", user);
            cmd.Parameters.AddWithValue("prod", prod);
            cmd.Parameters.AddWithValue("kiosk", kiosk);
            cmd.Parameters.AddWithValue("slot", slot);
            cmd.Parameters.AddWithValue("b", bouquet);
            cmd.Parameters.AddWithValue("o", order);
            cmd.Parameters.AddWithValue("chk", chk);
            await cmd.ExecuteNonQueryAsync();
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user, sellerId: seller));

        // 1. Query holiday=CHRISTMAS includes Dec 25
        var respXmas = await client.GetAsync($"/api/sellers/{seller}/ai/seasonal-sales?year=2026&holiday=CHRISTMAS");
        Assert.Equal(HttpStatusCode.OK, respXmas.StatusCode);
        var dataXmas = await respXmas.Content.ReadFromJsonAsync<SeasonalSalesResponse>();
        Assert.Equal(1, dataXmas!.TotalInvoices);

        // 2. Query holiday=NEW_YEAR includes Dec 25 (preceding window of Jan 1)
        var respNy = await client.GetAsync($"/api/sellers/{seller}/ai/seasonal-sales?year=2027&holiday=NEW_YEAR");
        Assert.Equal(HttpStatusCode.OK, respNy.StatusCode);
        var dataNy = await respNy.Content.ReadFromJsonAsync<SeasonalSalesResponse>();
        Assert.Equal(1, dataNy!.TotalInvoices);
    }

    [Fact]
    public async Task SeasonalForecast_RealHttp_Success_Failure_UnknownId()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var prod = Guid.NewGuid();
        var photoUrl = "https://res.cloudinary.com/florabot/image/upload/v1/florabot/forecast.jpg";
        var validSha = new string('f', 64);

        await using var conn = await data.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
                VALUES (@seller, 'Forecast Shop', '0907777777', 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30);
                INSERT INTO identity.users(id, email, password_hash, full_name, role, seller_id, status)
                VALUES (@user, @user::text || '@test.local', '!unprovisioned', 'Forecast User', 'SELLER', @seller, 'ACTIVE');

                INSERT INTO catalog.flower_products(id, seller_id, name, price, tags, shelf_life_hours, status, created_at)
                VALUES (@prod, @seller, 'Bó Hoa Hướng Dương', 320000, ARRAY['SUNFLOWER'], 48, 'ACTIVE', '2024-01-01 00:00:00+07');

                INSERT INTO notify.attachments(id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase)
                VALUES (gen_random_uuid(), 'catalog', 'flower_product', @prod, @photoUrl, 'image/jpeg', 2048, @validSha, 'PRODUCT');
                """;
            cmd.Parameters.AddWithValue("seller", seller);
            cmd.Parameters.AddWithValue("user", user);
            cmd.Parameters.AddWithValue("prod", prod);
            cmd.Parameters.AddWithValue("photoUrl", photoUrl);
            cmd.Parameters.AddWithValue("validSha", validSha);
            await cmd.ExecuteNonQueryAsync();
        }

        var req = new SeasonalForecastRequest(2026, 11, new DateOnly(2025, 11, 1), new DateOnly(2026, 10, 31));

        // 1. Upstream failure (HTTP 503) -> returns actual DB stats with null forecast & warning
        var failTransport = new TestForecastTransport(mode: "fail", targetProd: prod);
        using var appFail = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AI_SERVICE_URL", "http://ai.local:8000");
            builder.UseSetting("AI_SERVICE_TOKEN", "test-service-token-at-least-32-chars-long");
            builder.ConfigureServices(services =>
                services.AddHttpClient<SeasonalForecastClient>().ConfigurePrimaryHttpMessageHandler(() => failTransport));
        });

        using var clientFail = appFail.CreateClient();
        clientFail.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user, sellerId: seller));
        var fallbackResp = await clientFail.PostAsJsonAsync($"/api/sellers/{seller}/ai/seasonal-forecast", req);
        Assert.Equal(HttpStatusCode.OK, fallbackResp.StatusCode);
        var fallbackData = await fallbackResp.Content.ReadFromJsonAsync<SeasonalForecastResponse>();
        Assert.NotEmpty(fallbackData!.Warnings);
        var fallbackItem = Assert.Single(fallbackData.Products);
        Assert.Null(fallbackItem.ForecastInvoiceCount);
        Assert.Equal("INSUFFICIENT_HISTORY", fallbackItem.ModelStatus);
        Assert.Equal("FALLBACK", fallbackItem.ExplanationSource);

        // 2. Upstream unknown product ID -> rejected by ValidateResponse -> falls back
        var foreignTransport = new TestForecastTransport(mode: "foreign_id", targetProd: prod);
        using var appForeign = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AI_SERVICE_URL", "http://ai.local:8000");
            builder.UseSetting("AI_SERVICE_TOKEN", "test-service-token-at-least-32-chars-long");
            builder.ConfigureServices(services =>
                services.AddHttpClient<SeasonalForecastClient>().ConfigurePrimaryHttpMessageHandler(() => foreignTransport));
        });

        using var clientForeign = appForeign.CreateClient();
        clientForeign.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user, sellerId: seller));
        var foreignResp = await clientForeign.PostAsJsonAsync($"/api/sellers/{seller}/ai/seasonal-forecast", req);
        Assert.Equal(HttpStatusCode.OK, foreignResp.StatusCode);
        var foreignData = await foreignResp.Content.ReadFromJsonAsync<SeasonalForecastResponse>();
        Assert.NotEmpty(foreignData!.Warnings); // Fell back because upstream returned unknown ID

        // 3. Upstream valid success
        var successTransport = new TestForecastTransport(mode: "success", targetProd: prod);
        using var appSuccess = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AI_SERVICE_URL", "http://ai.local:8000");
            builder.UseSetting("AI_SERVICE_TOKEN", "test-service-token-at-least-32-chars-long");
            builder.ConfigureServices(services =>
                services.AddHttpClient<SeasonalForecastClient>().ConfigurePrimaryHttpMessageHandler(() => successTransport));
        });

        using var clientSuccess = appSuccess.CreateClient();
        clientSuccess.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role: "SELLER", userId: user, sellerId: seller));
        var successResp = await clientSuccess.PostAsJsonAsync($"/api/sellers/{seller}/ai/seasonal-forecast", req);
        Assert.Equal(HttpStatusCode.OK, successResp.StatusCode);
        var successData = await successResp.Content.ReadFromJsonAsync<SeasonalForecastResponse>();
        var successItem = Assert.Single(successData!.Products);
        Assert.Equal(prod, successItem.ProductId);
        Assert.Equal("Bó Hoa Hướng Dương", successItem.Name); // Authoritative name from DB
        Assert.Equal(28.5, successItem.ForecastInvoiceCount);
        Assert.Equal(35.0, successItem.ForecastUnits);
        Assert.Equal("POISSON_REGRESSION", successItem.ModelStatus);
        Assert.Equal("GEMINI", successItem.ExplanationSource);
        Assert.False(successItem.AnnualSeasonalityValidated);
    }

    private sealed class TestForecastTransport(string mode, Guid targetProd) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (mode == "fail")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

            var prodId = mode == "foreign_id" ? Guid.NewGuid() : targetProd;

            var responseObj = new UpstreamForecastResponse(
                ContractVersion: "seasonal-v1",
                AggregationLevel: "PRODUCT",
                DataSource: "DATABASE",
                HistoryStart: "2025-11-01",
                HistoryEnd: "2026-10-31",
                ForecastStart: "2026-11-01",
                ForecastEnd: "2026-11-30",
                Season: "AUTUMN",
                Holidays: [new UpstreamHoliday("TEACHERS_DAY", "Nhà giáo Việt Nam", "2026-11-20")],
                Warnings: [],
                Products:
                [
                    new UpstreamProductForecast(
                        ProductId: prodId,
                        Name: "Model Overridden Name",
                        ObservedInvoiceCount: 0,
                        ObservedUnits: 0,
                        SameSeasonInvoiceCount: 0,
                        ForecastInvoiceCount: 28.5,
                        ForecastUnits: 35.0,
                        ModelStatus: "POISSON_REGRESSION",
                        AnnualSeasonalityValidated: false,
                        Validation: new UpstreamValidation(90, 14, 1.2, 1.6, 0.09, 0.14),
                        Explanation: "Nhu cầu hoa hướng dương tăng trong tháng 11.",
                        ExplanationSource: "GEMINI"
                    )
                ]
            );

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(responseObj)
            });
        }
    }
}
