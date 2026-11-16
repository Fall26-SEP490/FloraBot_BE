using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Ordering;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class PreorderAccessoryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task SellerPaymentLinkAndWebhookChargeTheFlowerAndAccessoriesTotal()
    {
        using var f = await Setup();
        using var gateway = new KioskPaymentTests.SellerGateway(140000);
        using var app = factory.WithWebHostBuilder(builder =>
        {
            const string prefix = "PAYOS_CHANNELS:test-accessory-channel:";
            builder.UseSetting(prefix + "SellerId", f.Seller.ToString());
            builder.UseSetting(prefix + "ClientId", KioskPaymentTests.SellerGateway.ClientId);
            builder.UseSetting(prefix + "ApiKey", "test-api");
            builder.UseSetting(prefix + "ChecksumKey", KioskPaymentTests.SellerGateway.ChecksumKey);
            builder.UseSetting(prefix + "ExpectedBin", "970415");
            builder.UseSetting(prefix + "ExpectedAccount", "1010099887766");
            builder.UseSetting(prefix + "ExpectedName", "TIEM HOA TEST");
            builder.UseSetting(prefix + "ReturnUrl", "https://example.invalid/return");
            builder.UseSetting(prefix + "CancelUrl", "https://example.invalid/cancel");
            builder.ConfigureServices(services => services.AddSingleton<IPayOsClientResolver>(sp =>
                PayOsClientResolver.ForTest(new HttpClient(gateway, disposeHandler: false), sp.GetRequiredService<IConfiguration>())));
        });
        var request = await Request(f); await Quote(f, request);
        await Success(await Accept(f, await Row(f, request)));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = f.CustomerClient.DefaultRequestHeaders.Authorization;
        var response = await client.PostAsync($"/api/member/preorders/{request}/payment-link", null);
        await Success(response);
        var link = (await response.Content.ReadFromJsonAsync<PaymentLinkResponse>())!;
        Assert.Equal(140000, link.Amount);
        Assert.False(string.IsNullOrWhiteSpace(link.QrCode));
        var callback = JsonSerializer.SerializeToElement(new { orderCode = link.OrderCode, amount = link.Amount,
            reference = "preorder-accessory-" + request, code = "00", accountNumber = "1010099887766" });
        await Success(await client.PostAsJsonAsync("/api/payments/webhook", new { data = callback,
            signature = PayOsChecksum.Sign(callback, KioskPaymentTests.SellerGateway.ChecksumKey) }));
        var row = await Row(f, request);
        Assert.Equal("PAID", row.OrderStatus);
        Assert.Equal(3, await Stock(f));
        Assert.Equal("SELLER_DIRECT", await Sql(f, "SELECT funding_provenance::text FROM payment.payments WHERE checkout_id=@checkout AND kind='CHARGE'", ("checkout", row.CheckoutId!.Value)));
    }

    [Fact]
    public async Task QuoteAcceptanceUsesServerPricesReservesOnceAndShowsReceiptAndHistory()
    {
        using var f = await Setup();
        var request = await Request(f);
        Assert.Equal(5, await Stock(f));
        await Quote(f, request);
        var quote = await Row(f, request);
        Assert.Equal(100000, quote.QuotedFlowerPrice);
        Assert.Equal(40000, quote.QuotedAccessoryTotal);
        Assert.Equal(140000, quote.QuotedPrice);
        Assert.Equal(20000, Assert.Single(quote.QuotedAccessories).UnitPrice);
        Assert.Equal(5, await Stock(f));

        // A later catalog price/name change cannot alter the quote the customer saw.
        await Sql(f, "UPDATE kiosk_ops.accessories SET name='Renamed ribbon',price=30000 WHERE id=@accessory");
        await Success(await Accept(f, quote));
        await Success(await Accept(f, quote));
        Assert.Equal(3, await Stock(f));
        var ordered = await Row(f, request);
        Assert.Equal(140000L, await Sql(f, "SELECT amount FROM payment.payments WHERE checkout_id=@checkout AND kind='CHARGE'", ("checkout", ordered.CheckoutId!.Value)));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM ordering.orders WHERE kiosk_id=@kiosk"));

        var receiptResponse = await f.CustomerClient.PostAsJsonAsync("/api/receipts/lookup",
            new ReceiptLookupRequest(ordered.OrderId!.Value, ordered.TrackingToken!));
        await Success(receiptResponse);
        var receipt = (await receiptResponse.Content.ReadFromJsonAsync<ReceiptResponse>())!;
        Assert.Equal(140000, receipt.Total);
        Assert.Equal(140000, receipt.Items.Sum(x => x.Total));
        Assert.Contains(receipt.Items, x => x.Name == "Ribbon" && x.Quantity == 2 && x.UnitPrice == 20000);
        Assert.Contains(receipt.Items, x => x.Total == 100000 && x.Quantity == 1);
        var history = (await f.CustomerClient.GetFromJsonAsync<CustomerHistoryPage>("/api/member/history"))!;
        Assert.Equal(2, Assert.Single(history.Items).Items.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrExpireUnpaidReservationRestoresStockOnlyOnce(bool expire)
    {
        using var f = await Setup();
        var request = await Request(f); await Quote(f, request);
        await Success(await Accept(f, await Row(f, request)));
        var ordered = await Row(f, request);
        if (expire)
        {
            await Sql(f, "UPDATE ordering.orders SET created_at=public.app_now()-interval '1 hour' WHERE id=@order", ("order", ordered.OrderId!.Value));
            await Sql(f, "SELECT flow.expire_pending_payments()");
            await Sql(f, "SELECT flow.expire_pending_payments()");
        }
        else
        {
            await Success(await f.CustomerClient.PostAsJsonAsync($"/api/member/preorders/{request}/cancel", new { reason = "Customer cancels before payment" }));
            await Success(await f.CustomerClient.PostAsJsonAsync($"/api/member/preorders/{request}/cancel", new { reason = "Customer cancels before payment" }));
        }
        Assert.Equal(5, await Stock(f));
        Assert.Equal(expire ? "EXPIRED" : "CANCELLED", await Sql(f, "SELECT status FROM ordering.orders WHERE id=@order", ("order", ordered.OrderId!.Value)));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM ordering.order_items WHERE order_id=@order AND line_status='ACTIVE'", ("order", ordered.OrderId.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaidCancellationBeforeLoadingRefundsWholeQuoteAndRestoresAccessories(bool expire)
    {
        using var f = await Setup();
        var request = await Request(f); await Quote(f, request);
        await Success(await Accept(f, await Row(f, request)));
        var ordered = await Row(f, request); await Pay(f, ordered);
        Assert.Equal(HttpStatusCode.Conflict, (await f.CustomerClient.PostAsJsonAsync($"/api/member/preorders/{request}/cancel", new { reason = "Customer cannot self cancel a paid order" })).StatusCode);
        if (expire)
        {
            await Sql(f, "UPDATE ordering.web_requests SET pickup_at=public.app_now()-interval '3 hours',pickup_before=public.app_now()-interval '1 hour' WHERE id=@request", ("request", request));
            await Sql(f, "SELECT flow.expire_web_orders()");
            await Sql(f, "SELECT flow.expire_web_orders()");
        }
        else
        {
            await Success(await f.SellerClient.PostAsJsonAsync($"/api/sellers/{f.Seller}/preorders/{request}/cancel", new { reason = "Shop cannot prepare the order" }));
            await Success(await f.SellerClient.PostAsJsonAsync($"/api/sellers/{f.Seller}/preorders/{request}/cancel", new { reason = "Shop cannot prepare the order" }));
        }
        Assert.Equal(5, await Stock(f));
        Assert.Equal(140000L, await Sql(f, "SELECT sum(amount)::bigint FROM payment.payments WHERE order_id=@order AND kind='REFUND'", ("order", ordered.OrderId!.Value)));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM payment.payments WHERE order_id=@order AND kind='REFUND'", ("order", ordered.OrderId.Value)));
        Assert.Equal("DISPENSE_FAILED", await Sql(f, "SELECT status FROM ordering.orders WHERE id=@order", ("order", ordered.OrderId.Value)));
    }

    [Fact]
    public async Task StaleQuoteAndUnavailableStockRejectWithoutPartialOrderOrDeduction()
    {
        using var f = await Setup();
        var selection = new[] { new { accessoryId = f.Accessory, quantity = 2 }, new { accessoryId = f.SecondAccessory, quantity = 1 } };
        var request = await Request(f, selection); await Quote(f, request, selection);
        var oldQuote = await Row(f, request);
        await Quote(f, request, selection);
        Assert.Equal(HttpStatusCode.Conflict, (await Accept(f, oldQuote)).StatusCode);
        var quote = await Row(f, request);
        await Sql(f, "UPDATE kiosk_ops.accessories SET stock_quantity=0 WHERE id=@second", ("second", f.SecondAccessory));
        var rejected = await Accept(f, quote);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("PREORDER_ACCESSORY_OUT_OF_STOCK", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(5, await Stock(f));
        Assert.Equal(0L, await Sql(f, "SELECT count(*) FROM ordering.orders WHERE kiosk_id=@kiosk"));
        Assert.Equal("QUOTED", (await Row(f, request)).State);
    }

    [Fact]
    public async Task CompetingAcceptancesCannotOversellTheLastAccessory()
    {
        using var f = await Setup();
        await Sql(f, "UPDATE kiosk_ops.accessories SET stock_quantity=2 WHERE id=@accessory");
        var otherKiosk = Guid.NewGuid();
        await Sql(f, """
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,last_heartbeat_at,latitude,longitude)
              VALUES(@other,@other::text,'Other pickup kiosk','Other address','HCM',@other::text,@other::text,'ONLINE',public.app_now(),10.78,106.70);
            INSERT INTO kiosk_ops.slots(kiosk_id,slot_code,relay_channel,zone) VALUES(@other,'S01',0,'PREORDER');
            """, ("other", otherKiosk));
        await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(f.Data, otherKiosk, f.Seller);
        var first = await Request(f); var second = await Request(f, kiosk: otherKiosk);
        await Quote(f, first); await Quote(f, second);
        var responses = await Task.WhenAll(Accept(f, await Row(f, first)), Accept(f, await Row(f, second)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(0, await Stock(f));
        Assert.Equal(1L, await Sql(f, "SELECT count(*) FROM ordering.orders WHERE seller_id=@seller"));
        Assert.Equal(2L, await Sql(f, "SELECT sum(i.quantity)::bigint FROM ordering.order_items i JOIN ordering.orders o ON o.id=i.order_id WHERE o.seller_id=@seller"));
    }

    [Fact]
    public async Task CatalogAndSelectionRespectCustomerRoleKioskTenantAndRequestedQuantities()
    {
        using var f = await Setup();
        var path = $"/api/member/sellers/{f.Seller}/catalog/accessories";
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.SellerClient.GetAsync(path)).StatusCode);
        var response = await f.CustomerClient.GetAsync(path); await Success(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var catalog = (await response.Content.ReadFromJsonAsync<List<PreorderAccessoryCatalogItem>>())!;
        Assert.Equal(2, catalog.Count);
        Assert.DoesNotContain(catalog, x => x.Name is "Inactive" or "Empty" or "Wrong tenant");
        var compatibility = (await f.CustomerClient.GetFromJsonAsync<List<PreorderAccessoryCatalogItem>>($"/api/member/kiosks/{f.Kiosk}/catalog/accessories"))!;
        Assert.Equal(catalog.Select(x => x.Id), compatibility.Select(x => x.Id));

        var wrongTenant = await f.CustomerClient.PostAsJsonAsync("/api/member/preorders", RequestBody(f, Guid.NewGuid(), new[] { new { accessoryId = f.ForeignAccessory, quantity = 1 } }));
        Assert.Equal(HttpStatusCode.Conflict, wrongTenant.StatusCode);
        var forgedPrice = await f.CustomerClient.PostAsJsonAsync("/api/member/preorders", RequestBody(f, Guid.NewGuid(), new[] { new { accessoryId = f.Accessory, quantity = 1, unitPrice = 1 } }));
        Assert.Equal(HttpStatusCode.BadRequest, forgedPrice.StatusCode);
        var request = await Request(f);
        using var otherCustomer = factory.CreateClient();
        otherCustomer.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("CUSTOMER", userId: f.OtherCustomer));
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.PostAsJsonAsync($"/api/member/preorders/{request}/accept", new { price = 140000, pickupAt = f.Pickup, note = "Confirmed with accessories", quoteVersion = 1 })).StatusCode);
        var excess = await f.SellerClient.PostAsJsonAsync($"/api/sellers/{f.Seller}/preorders/{request}/quote", new { price = 100000, pickupAt = f.Pickup, note = "Confirmed with accessories", accessories = new[] { new { accessoryId = f.Accessory, quantity = 3 } } });
        Assert.Equal(HttpStatusCode.Conflict, excess.StatusCode);
        Assert.Equal(5, await Stock(f));
    }

    [Fact]
    public async Task SellerCreatesPricesAndRestocksShopAccessoryWithoutChoosingAKiosk()
    {
        using var f = await Setup();
        var path = $"/api/sellers/{f.Seller}/accessories";
        var response = await f.SellerClient.PostAsJsonAsync(path, new { name = "Bow priced by Seller", price = 10000 });
        await Success(response);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var created = (await f.SellerClient.GetFromJsonAsync<SellerAccessoryResponse>($"{path}/{id}"))!;
        Assert.Equal("SHOP", created.Scope); Assert.Null(created.KioskId); Assert.Null(created.KioskName);
        Assert.Equal(0, created.StockQuantity);
        var stockPath = $"/api/sellers/{f.Seller}/flows/restock_accessory";
        Assert.Equal(HttpStatusCode.NotFound, (await f.SellerClient.PostAsJsonAsync(stockPath, new { p_accessory = f.ForeignAccessory, p_qty = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.SellerClient.PostAsJsonAsync(stockPath, new { p_accessory = id, p_qty = 0 })).StatusCode);
        await Success(await f.SellerClient.PostAsJsonAsync($"/api/sellers/{f.Seller}/flows/restock_accessory", new { p_accessory = id, p_qty = 5 }));
        f.Accessory = id;
        var request = await Request(f); await Quote(f, request);
        var quote = await Row(f, request);
        Assert.Equal(20000, quote.QuotedAccessoryTotal); Assert.Equal(120000, quote.QuotedPrice);
        Assert.Equal(5, await Stock(f));
        await Success(await Accept(f, quote)); Assert.Equal(3, await Stock(f));
        Assert.Equal(HttpStatusCode.Conflict, (await f.SellerClient.PostAsJsonAsync(stockPath, new { p_accessory = id, p_qty = int.MaxValue })).StatusCode);
        Assert.Equal(3, await Stock(f));
        var duplicate = await f.SellerClient.PostAsJsonAsync(path, new { name = "bow PRICED by seller", price = 5000 });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var inventory = (await f.SellerClient.GetFromJsonAsync<List<SellerAccessoryResponse>>(path + "?scope=SHOP"))!;
        Assert.All(inventory, x => { Assert.Equal("SHOP", x.Scope); Assert.Null(x.KioskId); });
        var changed = await f.SellerClient.PutAsJsonAsync($"{path}/{id}", new { name = "Bow priced by Seller", price = 15000 });
        await Success(changed);
        Assert.Equal(10000, Assert.Single((await Row(f, request)).QuotedAccessories).UnitPrice);
        await Success(await f.SellerClient.PatchAsJsonAsync($"{path}/{id}/status", new { status = "INACTIVE" }));
        var catalog = (await f.CustomerClient.GetFromJsonAsync<List<PreorderAccessoryCatalogItem>>($"/api/member/sellers/{f.Seller}/catalog/accessories"))!;
        Assert.DoesNotContain(catalog, x => x.Id == id);
    }

    [Fact]
    public async Task AccessoriesAreOptionalAndShopStockCannotBeBoughtAsKioskStock()
    {
        using var f = await Setup();
        var request = await Request(f, Array.Empty<object>()); await Quote(f, request, Array.Empty<object>());
        var quote = await Row(f, request);
        Assert.Empty(quote.QuotedAccessories); Assert.Equal(100000, quote.QuotedPrice);
        await Success(await Accept(f, quote)); Assert.Equal(5, await Stock(f));
        using var device = factory.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key", "test-preorder-" + f.Kiosk);
        var physicalCatalog = (await device.GetFromJsonAsync<List<KioskAccessoryResponse>>($"/api/kiosks/{f.Kiosk}/catalog/accessories"))!;
        Assert.Empty(physicalCatalog);
        var checkout = await device.PostAsJsonAsync($"/api/kiosks/{f.Kiosk}/flows/kiosk_checkout", new { p_bouquets = Array.Empty<Guid>(), p_accessories = new[] { new { id = f.Accessory, qty = 1 } } });
        Assert.Equal(HttpStatusCode.Conflict, checkout.StatusCode); Assert.Equal(5, await Stock(f));
    }

    [Fact]
    public async Task OldKioskAccessoryQuotesRemainAcceptableButNewRequestsUseShopInventory()
    {
        using var f = await Setup();
        var legacy = Guid.NewGuid();
        await Sql(f, "INSERT INTO kiosk_ops.accessories(id,seller_id,kiosk_id,name,price,stock_quantity) VALUES(@legacy,@seller,@kiosk,'Legacy kiosk bow',10000,5)", ("legacy", legacy));
        var selection = new[] { new { accessoryId = legacy, quantity = 2 } };
        var rejected = await f.CustomerClient.PostAsJsonAsync("/api/member/preorders", RequestBody(f, Guid.NewGuid(), selection));
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains("PREORDER_ACCESSORY_NOT_IN_SHOP", await rejected.Content.ReadAsStringAsync());
        var request = await Request(f, Array.Empty<object>());
        // Reconstruct a request stored by the preceding API version, retaining its accessory IDs.
        await Sql(f, """
            UPDATE ordering.web_requests SET requested_accessories=jsonb_build_array(jsonb_build_object(
              'accessoryId',@legacy,'name','Legacy kiosk bow','quantity',2)) WHERE id=@request;
            """, ("legacy", legacy), ("request", request));
        await Quote(f, request, selection); await Success(await Accept(f, await Row(f, request)));
        Assert.Equal(3, await Sql(f, "SELECT stock_quantity FROM kiosk_ops.accessories WHERE id=@legacy", ("legacy", legacy)));
        await Success(await f.CustomerClient.PostAsJsonAsync($"/api/member/preorders/{request}/cancel", new { reason = "Cancel the historical quote safely" }));
        Assert.Equal(5, await Sql(f, "SELECT stock_quantity FROM kiosk_ops.accessories WHERE id=@legacy", ("legacy", legacy)));
    }

    [Fact]
    public async Task LoadedAccessoriesShareBouquetSlotAndPickupDoesNotRestoreSoldStock()
    {
        using var f = await Setup();
        var request = await Request(f); await Quote(f, request);
        await Success(await Accept(f, await Row(f, request)));
        var ordered = await Row(f, request); await Pay(f, ordered);
        var task = Guid.NewGuid();
        await Sql(f, "SELECT flow.assign_preorder_staff_task(@task,@actor,@staff,@request,@slot,'Load bouquet and requested accessories')", ("task", task), ("staff", f.Staff), ("request", request), ("slot", f.Slot));
        await Sql(f, "SELECT flow.transition_staff_task(@task,@staff,1,'IN_PROGRESS','Preparing the bouquet and accessories')", ("task", task), ("staff", f.Staff));
        using var staff = factory.CreateClient();
        staff.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER_STAFF", userId: f.Staff, sellerId: f.Seller));
        var options = (await staff.GetFromJsonAsync<StaffPreorderOptions>($"/api/staff/tasks/{task}/preorders"))!;
        Assert.Equal(2, Assert.Single(Assert.Single(options.Preorders).Accessories).Quantity);
        using var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("X-Kiosk-Key", "test-preorder-" + f.Kiosk);
        await PreorderLoadingTestFlow.LoadAsync(f.Data, factory, staff, device, request, f.Kiosk, f.Slot, f.Staff, 0,
            $"/api/staff/tasks/{task}/preorders/{request}/loading-access",
            $"/api/staff/tasks/{task}/preorders/{request}/fulfill");
        Assert.Equal(1L, await Sql(f, "SELECT count(DISTINCT slot_id) FROM ordering.order_items WHERE order_id=@order", ("order", ordered.OrderId!.Value)));
        Assert.Equal(140000L, await Sql(f, "SELECT sum(line_total)::bigint FROM ordering.order_items WHERE order_id=@order", ("order", ordered.OrderId.Value)));
        Assert.Equal(3, await Stock(f));
        await Sql(f, """
            SELECT flow.issue_pickup_tokens(@order);
            SELECT flow.set_order_status(@order,'DISPENSING','SYSTEM');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'SENT');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'ACK');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'OPENED');
            SELECT flow.device_event((SELECT cmd_id FROM kiosk_ops.unlock_tokens WHERE order_id=@order),'CLOSED');
            """, ("order", ordered.OrderId.Value));
        Assert.Equal("COMPLETED", await Sql(f, "SELECT status FROM ordering.orders WHERE id=@order", ("order", ordered.OrderId.Value)));
        Assert.Equal(3, await Stock(f));
        Assert.Equal(HttpStatusCode.Conflict, (await f.SellerClient.PostAsJsonAsync($"/api/sellers/{f.Seller}/preorders/{request}/cancel", new { reason = "Cannot cancel already collected order" })).StatusCode);
    }

    private sealed class Fixture : IDisposable
    {
        public Guid Seller = Guid.NewGuid(), Actor = Guid.NewGuid(), Staff = Guid.NewGuid(), Customer = Guid.NewGuid(), OtherCustomer = Guid.NewGuid();
        public Guid Kiosk = Guid.NewGuid(), Product = Guid.NewGuid(), Slot = Guid.NewGuid(), ForeignSeller = Guid.NewGuid();
        public Guid Accessory = Guid.Parse("10000000-0000-0000-0000-" + Guid.NewGuid().ToString("N")[20..]),
            SecondAccessory = Guid.Parse("20000000-0000-0000-0000-" + Guid.NewGuid().ToString("N")[20..]), ForeignAccessory = Guid.NewGuid();
        public DateTimeOffset Pickup = DateTimeOffset.UtcNow.AddHours(3);
        public required NpgsqlDataSource Data;
        public required HttpClient CustomerClient, SellerClient;
        public void Dispose() { CustomerClient.Dispose(); SellerClient.Dispose(); }
    }

    private async Task<Fixture> Setup()
    {
        var f = new Fixture { Data = factory.Services.GetRequiredService<NpgsqlDataSource>(), CustomerClient = factory.CreateClient(), SellerClient = factory.CreateClient() };
        f.CustomerClient.DefaultRequestHeaders.Add("X-Test-Client-IP", $"127.100.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        f.CustomerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("CUSTOMER", userId: f.Customer));
        f.SellerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: f.Actor, sellerId: f.Seller));
        await Sql(f, """
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
              VALUES(@seller,'Preorder accessory shop','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000d',CURRENT_DATE+365),
                (@foreignSeller,'Other accessory shop','0901112234','ACTIVE','20000000-0000-0000-0000-00000000000d',CURRENT_DATE+365);
            INSERT INTO identity.users(id,email,full_name,role,seller_id,password_hash)
              VALUES(@actor,@actor::text||'@example.invalid','Shop','SELLER',@seller,'!unprovisioned'),
                (@staff,@staff::text||'@example.invalid','Staff','SELLER_STAFF',@seller,'!unprovisioned');
            INSERT INTO identity.users(id,email,full_name,role)
              VALUES(@customer,@customer::text||'@example.invalid','Customer','CUSTOMER'),
                (@otherCustomer,@otherCustomer::text||'@example.invalid','Other','CUSTOMER');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,quantity)
              VALUES(@product,@seller,'Custom bouquet',100000,48,'ACTIVE',20);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,last_heartbeat_at,latitude,longitude,api_key_hash)
              VALUES(@kiosk,@kiosk::text,'Accessory kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',public.app_now(),10.77,106.69,
                encode(sha256(convert_to('test-preorder-'||@kiosk::text,'UTF8')),'hex'));
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone)
              VALUES(@slot,@kiosk,'S01',0,'PREORDER'),(public.uuid_v7(),@kiosk,'S02',1,'PREORDER'),(public.uuid_v7(),@kiosk,'S03',2,'PREORDER');
            """ + CurrentMainKioskFixture.AssignKioskWithLeaseSql + """
            INSERT INTO kiosk_ops.accessories(id,seller_id,name,price,stock_quantity,status)
              VALUES(@accessory,@seller,'Ribbon',20000,5,'ACTIVE'),(@second,@seller,'Card',5000,5,'ACTIVE'),
                (@foreignAccessory,@foreignSeller,'Wrong tenant',5000,5,'ACTIVE'),
                (public.uuid_v7(),@seller,'Inactive',1000,5,'INACTIVE'),(public.uuid_v7(),@seller,'Empty',1000,0,'ACTIVE');
            INSERT INTO payment.payment_channel_versions(id,seller_id,version,secret_ref,expected_bank_bin,expected_bank_account,expected_bank_name,state,verified_at)
              VALUES(public.uuid_v7(),@seller,1,'test-accessory-channel','970415','1010099887766','TIEM HOA TEST','ACTIVE',public.app_now());
            """, ("staff", f.Staff), ("otherCustomer", f.OtherCustomer), ("foreignSeller", f.ForeignSeller), ("second", f.SecondAccessory), ("foreignAccessory", f.ForeignAccessory), ("slot", f.Slot));
        return f;
    }

    private static object Selection(Fixture f) => new[] { new { accessoryId = f.Accessory, quantity = 2 } };
    private static object RequestBody(Fixture f, Guid id, object selection, Guid? kiosk = null) => new { id, kind = "CUSTOM", productId = f.Product, kioskId = kiosk ?? f.Kiosk, pickupAt = f.Pickup, instructions = "Birthday bouquet with requested accessories", accessories = selection };
    private static async Task<Guid> Request(Fixture f, object? selection = null, Guid? kiosk = null)
    {
        var id = Guid.NewGuid();
        await Success(await f.CustomerClient.PostAsJsonAsync("/api/member/preorders", RequestBody(f, id, selection ?? Selection(f), kiosk)));
        await Success(await f.CustomerClient.PostAsJsonAsync("/api/member/preorders", RequestBody(f, id, selection ?? Selection(f), kiosk)));
        return id;
    }
    private static async Task Quote(Fixture f, Guid request, object? selection = null) => await Success(await f.SellerClient.PostAsJsonAsync($"/api/sellers/{f.Seller}/preorders/{request}/quote", new { price = 100000, pickupAt = f.Pickup, note = "Confirmed with accessories", accessories = selection ?? Selection(f) }));
    private static Task<HttpResponseMessage> Accept(Fixture f, WebRequestRow quote) => f.CustomerClient.PostAsJsonAsync($"/api/member/preorders/{quote.Id}/accept", new { price = quote.QuotedPrice, pickupAt = quote.PickupAt, note = quote.QuoteNote, quoteVersion = quote.QuoteVersion });
    private static async Task<WebRequestRow> Row(Fixture f, Guid request) => (await f.CustomerClient.GetFromJsonAsync<WebRequestPage>("/api/member/preorders"))!.Items.Single(x => x.Id == request);
    private static async Task<int> Stock(Fixture f) => (int)(await Sql(f, "SELECT stock_quantity FROM kiosk_ops.accessories WHERE id=@accessory"))!;
    private static Task<object?> Pay(Fixture f, WebRequestRow ordered) => Sql(f, """
        UPDATE payment.payments p SET seller_id=c.seller_id,channel_id=c.id,channel_version=c.version,
          receiver_snapshot=jsonb_build_object('bin',c.expected_bank_bin,'accountNumber',c.expected_bank_account,
            'accountName',c.expected_bank_name,'provider','PAYOS'),funding_provenance='SELLER_DIRECT',is_direct_receipt=true
        FROM payment.payment_channel_versions c
        WHERE p.checkout_id=@checkout AND p.kind='CHARGE' AND c.seller_id=@seller AND c.version=1;
        SELECT flow.checkout_paid(@checkout,@txn,true,@amount)
        """, ("checkout", ordered.CheckoutId!.Value), ("txn", "test-paid-" + ordered.Id), ("amount", ordered.QuotedPrice!.Value));
    private static async Task Success(HttpResponseMessage response) => Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    private static async Task<object?> Sql(Fixture f, string sql, params (string Key, object Value)[] args)
    {
        await using var command = f.Data.CreateCommand(sql);
        foreach (var (key, value) in new[] { ("seller", f.Seller), ("actor", f.Actor), ("customer", f.Customer), ("kiosk", f.Kiosk), ("product", f.Product), ("accessory", f.Accessory) }) command.Parameters.AddWithValue(key, value);
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value);
        return await command.ExecuteScalarAsync();
    }
}
