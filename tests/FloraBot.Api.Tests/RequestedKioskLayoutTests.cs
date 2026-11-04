using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class RequestedKioskLayoutTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Applications = "/api/member/kiosk-applications";
    private const string Operations = "/api/operations/kiosk-applications";
    private const string Pricing = "/api/operations/kiosk-pricing";
    private sealed record Slot(Guid Id, string Zone, string Status);

    // One scenario deliberately serializes edits to the shared pricing configuration. All
    // accounts, applications, hardware and financial fixtures belong to this test only.
    [Fact]
    public async Task RequestedLayoutUsesVersionedPricesAndImmutableZones()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var managerId = await User(data, "OPERATIONS_MANAGER");
        var customerId = await User(data, "CUSTOMER");
        var secondCustomerId = await User(data, "CUSTOMER");
        var technicianId = await User(data, "TECHNICIAN");
        var adminId = await User(data, "ADMIN");
        using var manager = Client("OPERATIONS_MANAGER", managerId);
        using var customer = Client("CUSTOMER", customerId);
        using var otherCustomer = Client("CUSTOMER", secondCustomerId);
        using var technician = Client("TECHNICIAN", technicianId);
        using var admin = Client("ADMIN", adminId);

        var openApi = await Get(admin, "/openapi/v1.json");
        var schemas = openApi.GetProperty("components").GetProperty("schemas");
        var createSchema = schemas.EnumerateObject().Single(x => x.Name.EndsWith("CreateKioskApplicationRequest")).Value;
        Assert.True(createSchema.GetProperty("properties").TryGetProperty("pricingVersion", out _));
        var pricingSchema = schemas.EnumerateObject().Single(x => x.Name.EndsWith("KioskSlotPricingResponse")).Value;
        foreach (var name in new[] { "walkInAnnualPrice", "preorderAnnualPrice", "version", "configured", "termMonths" })
            Assert.True(pricingSchema.GetProperty("properties").TryGetProperty(name, out _));
        Assert.DoesNotContain(schemas.EnumerateObject(), x => x.Name.EndsWith("AcceptKioskQuoteRequest"));
        Assert.True(openApi.GetProperty("paths").GetProperty(Pricing).TryGetProperty("put", out _));
        Assert.DoesNotContain(openApi.GetProperty("paths").EnumerateObject(),
            x => x.Name.Contains("/slots/") && x.Name.EndsWith("/zone"));

        var originalPricing = await Get(manager, "/api/kiosk-packages");
        var originalVersion = originalPricing.GetProperty("version").GetInt32();
        await Status(HttpStatusCode.Forbidden, customer.PutAsJsonAsync(Pricing, new
        {
            walkInAnnualPrice = 100000,
            preorderAnnualPrice = 200000,
            expectedVersion = originalVersion
        }));
        await Status(HttpStatusCode.BadRequest, manager.PutAsJsonAsync(Pricing, new
        {
            walkInAnnualPrice = -1,
            preorderAnnualPrice = 200000,
            expectedVersion = originalVersion
        }));
        var firstPricing = await Json(manager.PutAsJsonAsync(Pricing, new
        {
            walkInAnnualPrice = 100000,
            preorderAnnualPrice = 200000,
            expectedVersion = originalVersion
        }));
        var firstVersion = firstPricing.GetProperty("version").GetInt32();
        Assert.True(firstVersion > originalVersion);
        var advertised = await Get(customer, "/api/kiosk-packages");
        Assert.True(advertised.GetProperty("configured").GetBoolean());
        Assert.Equal(12, advertised.GetProperty("termMonths").GetInt32());
        Assert.Equal(100000, advertised.GetProperty("walkInAnnualPrice").GetInt64());
        Assert.Equal(200000, advertised.GetProperty("preorderAnnualPrice").GetInt64());

        // Money comes from the versioned server configuration. Unknown money fields
        // are rejected by the strict JSON contract; the known legacy package is ignored.
        await Status(HttpStatusCode.BadRequest, customer.PostAsJsonAsync(Applications,
            ApplicationBody(customerId, null)));
        var tampered = ApplicationBody(customerId, firstVersion);
        tampered["annualFee"] = 1;
        await Status(HttpStatusCode.BadRequest, customer.PostAsJsonAsync(Applications, tampered));
        var application = await Json(customer.PostAsJsonAsync(Applications,
            ApplicationBody(customerId, firstVersion)));
        var applicationId = application.GetProperty("id").GetGuid();
        var paymentId = application.GetProperty("paymentId").GetGuid();
        Assert.Equal("AWAITING_PAYMENT", application.GetProperty("status").GetString());
        AssertPrices(application, firstVersion, 100000, 200000, 2, 2, 600000);
        await AssertFinancialSnapshot(data, applicationId, firstVersion, 100000, 200000, 600000, "PENDING");

        var secondPricing = await Json(manager.PutAsJsonAsync(Pricing, new
        {
            walkInAnnualPrice = 120000,
            preorderAnnualPrice = 250000,
            expectedVersion = firstVersion
        }));
        var secondVersion = secondPricing.GetProperty("version").GetInt32();
        Assert.True(secondVersion > firstVersion);
        await Status(HttpStatusCode.Conflict, manager.PutAsJsonAsync(Pricing, new
        {
            walkInAnnualPrice = 1,
            preorderAnnualPrice = 1,
            expectedVersion = firstVersion
        }));
        await Status(HttpStatusCode.Conflict, otherCustomer.PostAsJsonAsync(Applications,
            ApplicationBody(secondCustomerId, firstVersion)));
        AssertPrices(await Get(manager, $"{Operations}/{applicationId}"), firstVersion, 100000, 200000, 2, 2, 600000);
        await AssertFinancialSnapshot(data, applicationId, firstVersion, 100000, 200000, 600000, "PENDING");

        var preorderOnly = ApplicationBody(secondCustomerId, secondVersion);
        preorderOnly.Remove("packageId");
        preorderOnly["walkInSlots"] = 0;
        preorderOnly["preorderSlots"] = 4;
        var otherApplication = await Json(otherCustomer.PostAsJsonAsync(Applications, preorderOnly));
        var otherApplicationId = otherApplication.GetProperty("id").GetGuid();
        AssertPrices(otherApplication, secondVersion, 120000, 250000, 0, 4, 1000000);

        // A physical kiosk cannot be provisioned before the payment and Manager review.
        await Status(HttpStatusCode.Conflict, manager.PostAsJsonAsync("/api/operations/kiosks/enroll", Enroll(applicationId)));
        await SettleAndConfirm(data, manager, applicationId, paymentId);
        await SettleAndConfirm(data, manager, otherApplicationId, otherApplication.GetProperty("paymentId").GetGuid());
        var kiosk = await Json(manager.PostAsJsonAsync("/api/operations/kiosks/enroll", Enroll(applicationId)));
        var kioskId = kiosk.GetProperty("id").GetGuid();
        Assert.Equal(4, kiosk.GetProperty("slotCount").GetInt32());
        Assert.Equal(2, kiosk.GetProperty("walkInSlots").GetInt32());
        Assert.Equal(2, kiosk.GetProperty("preorderSlots").GetInt32());
        Assert.Equal(applicationId, kiosk.GetProperty("applicationId").GetGuid());
        var slots = await Slots(data, kioskId);
        Assert.Equal(4, slots.Count);
        Assert.Equal(2, slots.Count(x => x.Zone == "WALK_IN"));
        Assert.Equal(2, slots.Count(x => x.Zone == "PREORDER"));
        Assert.All(slots, x => Assert.Equal("FREE", x.Status));
        await Status(HttpStatusCode.Conflict, manager.PostAsJsonAsync("/api/operations/kiosks/enroll", Enroll(applicationId)));

        var ownEligible = await Get(manager, $"/api/operations/eligible-kiosks?applicationId={applicationId}");
        Assert.Contains(ownEligible.EnumerateArray(), x => x.GetProperty("id").GetGuid() == kioskId);
        var othersEligible = await Get(manager, $"/api/operations/eligible-kiosks?applicationId={otherApplicationId}");
        Assert.DoesNotContain(othersEligible.EnumerateArray(), x => x.GetProperty("id").GetGuid() == kioskId);
        await Status(HttpStatusCode.Conflict, manager.PostAsJsonAsync($"{Operations}/{otherApplicationId}/schedule",
            Schedule(technicianId, kioskId)));

        var walkSlot = slots.First(x => x.Zone == "WALK_IN").Id;
        await AssertImmutableLayout(data, manager, kioskId, walkSlot, applicationId);
        AssertPrices(await Get(manager, $"{Operations}/{applicationId}"), firstVersion, 100000, 200000, 2, 2, 600000);

        await Json(manager.PostAsJsonAsync($"{Operations}/{applicationId}/schedule", Schedule(technicianId, kioskId)));
        await Json(technician.PostAsJsonAsync($"/api/staff/kiosk-installations/{applicationId}/start", new { }));
        // A local metadata fixture stands in for evidence. No photo is uploaded and
        // no external Cloudinary, email or PayOS request is made by this test.
        await Sql(data, """
            SELECT flow.attach_kiosk_evidence(@id,@application,
              'https://res.cloudinary.com/demo/image/upload/test-only.jpg',
              'image/jpeg',1024,'test-only-hash',@actor);
            """, ("id", Guid.NewGuid()), ("application", applicationId), ("actor", technicianId));
        await Json(technician.PostAsJsonAsync($"/api/staff/kiosk-installations/{applicationId}/submit",
            new { report = "Test fixture installation complete, not a real device visit" }));
        var handedOver = await Json(manager.PostAsJsonAsync($"{Operations}/{applicationId}/handover", new { }));
        Assert.Equal("ACTIVE", handedOver.GetProperty("status").GetString());
        var sellerId = handedOver.GetProperty("sellerId").GetGuid();
        AssertPrices(handedOver, firstVersion, 100000, 200000, 2, 2, 600000);
        Assert.All(await Slots(data, kioskId), x => Assert.Equal("RENTED_EMPTY", x.Status));
        await using (var lease = data.CreateCommand("SELECT price FROM identity.subscriptions WHERE kiosk_id=@kiosk AND seller_id=@seller AND status='ACTIVE'"))
        {
            lease.Parameters.AddWithValue("kiosk", kioskId);
            lease.Parameters.AddWithValue("seller", sellerId);
            Assert.Equal(600000L, (long)(await lease.ExecuteScalarAsync())!);
        }

        using var seller = Client("SELLER", customerId, sellerId);
        await Status(HttpStatusCode.NotFound, seller.PutAsJsonAsync(Zone(kioskId, walkSlot),
            new { zone = "PREORDER", reason = "Seller cannot configure hardware" }));
        await Status(HttpStatusCode.NotFound, seller.PutAsJsonAsync($"/api/me/kiosks/{kioskId}/slots/{walkSlot}/zone",
            new { zone = "PREORDER" }));
        await using (var oldFlow = data.CreateCommand("SELECT flow.seller_set_slot_zone(@kiosk,@slot,@actor,'PREORDER')"))
        {
            oldFlow.Parameters.AddWithValue("kiosk", kioskId);
            oldFlow.Parameters.AddWithValue("slot", walkSlot);
            oldFlow.Parameters.AddWithValue("actor", customerId);
            var denied = await Assert.ThrowsAsync<PostgresException>(async () => { await oldFlow.ExecuteNonQueryAsync(); });
            Assert.Equal("42501", denied.SqlState);
        }
        await AssertImmutableLayout(data, manager, kioskId, walkSlot, applicationId);
        Assert.Equal(2, (await Slots(data, kioskId)).Count(x => x.Zone == "PREORDER"));
        AssertPrices(await Get(manager, $"{Operations}/{applicationId}"), firstVersion, 100000, 200000, 2, 2, 600000);
        await AssertFinancialSnapshot(data, applicationId, firstVersion, 100000, 200000, 600000, "SUCCEEDED");

        // The next kiosk uses current prices, retaining the existing shop and bank.
        var additional = await Json(seller.PostAsJsonAsync("/api/me/kiosk-applications", new
        {
            installationAddress = "Second branch test address",
            latitude = 10.81,
            longitude = 106.72,
            walkInSlots = 1,
            preorderSlots = 1,
            pricingVersion = secondVersion
        }));
        Assert.Equal(sellerId, additional.GetProperty("sellerId").GetGuid());
        Assert.Equal("AWAITING_PAYMENT", additional.GetProperty("status").GetString());
        AssertPrices(additional, secondVersion, 120000, 250000, 1, 1, 370000);
        foreach (var name in new[] { "shopName", "phone", "contactEmail", "bankName", "bankHolder", "bankAccountSuffix" })
            Assert.Equal(handedOver.GetProperty(name).GetString(), additional.GetProperty(name).GetString());
        await using var profiles = data.CreateCommand("""
            SELECT old.seller_id=new.seller_id AND old.bank_account_no_enc=new.bank_account_no_enc
              AND old.shop_name=new.shop_name AND s.shop_name=old.shop_name
              AND s.bank_account_no_enc=old.bank_account_no_enc
            FROM kiosk_ops.kiosk_applications old
            JOIN kiosk_ops.kiosk_applications new ON new.id=@new
            JOIN identity.sellers s ON s.id=old.seller_id WHERE old.id=@old;
            """);
        profiles.Parameters.AddWithValue("old", applicationId);
        profiles.Parameters.AddWithValue("new", additional.GetProperty("id").GetGuid());
        Assert.True((bool)(await profiles.ExecuteScalarAsync())!);
    }

    private static async Task AssertImmutableLayout(NpgsqlDataSource data, HttpClient manager,
        Guid kioskId, Guid slotId, Guid applicationId)
    {
        await Status(HttpStatusCode.NotFound, manager.PutAsJsonAsync(Zone(kioskId, slotId),
            new { zone = "PREORDER", reason = "No role may reclassify an empty slot" }));
        foreach (var sql in new[] {
            "UPDATE kiosk_ops.slots SET zone='PREORDER' WHERE id=@slot",
            "UPDATE kiosk_ops.kiosk_applications SET walk_in_slots=1,preorder_slots=3 WHERE id=@app",
            "SELECT flow.manager_set_slot_zone(@kiosk,@slot,@actor,'PREORDER','No later changes')"
        })
        {
            await using var command = data.CreateCommand(sql);
            command.Parameters.AddWithValue("kiosk", kioskId);
            command.Parameters.AddWithValue("slot", slotId);
            command.Parameters.AddWithValue("app", applicationId);
            command.Parameters.AddWithValue("actor", Guid.NewGuid());
            var denied = await Assert.ThrowsAsync<PostgresException>(async () => { await command.ExecuteNonQueryAsync(); });
            Assert.Equal("42501", denied.SqlState);
        }
        // Other slot attributes and same-value updates remain valid.
        await Sql(data, "UPDATE kiosk_ops.slots SET zone=zone WHERE id=@slot", ("slot", slotId));
    }

    private HttpClient Client(string role, Guid user, Guid? seller = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role, userId: user, sellerId: seller));
        return client;
    }

    private static Dictionary<string, object?> ApplicationBody(Guid customerId, int? version) => new()
    {
        ["shopName"] = "Layout test shop",
        ["phone"] = "0901234567",
        ["contactEmail"] = customerId.ToString("N") + "@example.invalid",
        ["installationAddress"] = "First branch test address",
        ["latitude"] = 10.78,
        ["longitude"] = 106.69,
        ["bankName"] = "TEST BANK",
        ["bankHolder"] = "TEST CUSTOMER",
        ["bankAccountNumber"] = "1234567890",
        ["walkInSlots"] = 2,
        ["preorderSlots"] = 2,
        ["pricingVersion"] = version,
        ["packageId"] = Guid.NewGuid()
    };

    private static object Enroll(Guid applicationId) => new
    {
        code = "K-LAYOUT-" + Guid.NewGuid().ToString("N")[..12],
        name = "Requested layout test kiosk",
        hardwareId = "TEST-" + Guid.NewGuid().ToString("N"),
        mqttClientId = "TEST-" + Guid.NewGuid().ToString("N"),
        region = "HCM",
        applicationId
    };

    private static object Schedule(Guid technicianId, Guid kioskId) => new
    {
        technicianId,
        kioskId,
        scheduledDate = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd"),
        timeWindow = "09:00 - 12:00"
    };

    private static string Zone(Guid kiosk, Guid slot) => $"/api/operations/kiosks/{kiosk}/slots/{slot}/zone";

    private static async Task<Guid> User(NpgsqlDataSource data, string role)
    {
        var id = Guid.NewGuid();
        await Sql(data, """
            INSERT INTO identity.users(id,email,password_hash,full_name,role,status)
            VALUES(@id,@id::text || '@example.invalid','!test-only','Layout test actor',@role,'ACTIVE');
            """, ("id", id), ("role", role));
        return id;
    }

    private static async Task SettleAndConfirm(NpgsqlDataSource data, HttpClient manager, Guid application, Guid payment)
    {
        await Sql(data, "SELECT flow.kiosk_application_paid(@payment,@reference)",
            ("payment", payment), ("reference", "TEST-LAYOUT-" + Guid.NewGuid().ToString("N")));
        var confirmed = await Json(manager.PostAsJsonAsync($"{Operations}/{application}/confirm",
            new { note = "Test contact and location confirmed" }));
        Assert.Equal("CONFIRMED", confirmed.GetProperty("status").GetString());
    }

    private static void AssertPrices(JsonElement app, int version, long walkPrice, long prePrice, int walk, int pre, long fee)
    {
        Assert.Equal("SLOT_PRICING", app.GetProperty("pricingMode").GetString());
        Assert.Equal(version, app.GetProperty("pricingVersion").GetInt32());
        Assert.Equal(walkPrice, app.GetProperty("walkInAnnualPrice").GetInt64());
        Assert.Equal(prePrice, app.GetProperty("preorderAnnualPrice").GetInt64());
        Assert.Equal(walk, app.GetProperty("walkInSlots").GetInt32());
        Assert.Equal(pre, app.GetProperty("preorderSlots").GetInt32());
        Assert.Equal(walk + pre, app.GetProperty("totalSlots").GetInt32());
        Assert.Equal(fee, app.GetProperty("annualFee").GetInt64());
    }

    private static async Task AssertFinancialSnapshot(NpgsqlDataSource data, Guid application, int version,
        long walkPrice, long prePrice, long fee, string paymentStatus)
    {
        await using var command = data.CreateCommand("""
            SELECT a.pricing_version,a.walk_in_annual_price,a.preorder_annual_price,a.annual_fee,p.amount,p.status
            FROM kiosk_ops.kiosk_applications a JOIN payment.payments p ON p.id=a.payment_id WHERE a.id=@application;
            """);
        command.Parameters.AddWithValue("application", application);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(version, reader.GetInt32(0));
        Assert.Equal(walkPrice, reader.GetInt64(1));
        Assert.Equal(prePrice, reader.GetInt64(2));
        Assert.Equal(fee, reader.GetInt64(3));
        Assert.Equal(fee, reader.GetInt64(4));
        Assert.Equal(paymentStatus, reader.GetString(5));
    }

    private static async Task<List<Slot>> Slots(NpgsqlDataSource data, Guid kiosk)
    {
        await using var command = data.CreateCommand("SELECT id,zone,status FROM kiosk_ops.slots WHERE kiosk_id=@kiosk ORDER BY slot_code");
        command.Parameters.AddWithValue("kiosk", kiosk);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Slot>();
        while (await reader.ReadAsync()) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    private static async Task Sql(NpgsqlDataSource data, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = data.CreateCommand(sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Status(HttpStatusCode expected, Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task<JsonElement> Get(HttpClient client, string path)
    {
        var result = await Json(client.GetAsync(path));
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("application", out var application)
            ? application.Clone() : result;
    }

    private static async Task<JsonElement> Json(Task<HttpResponseMessage> pending)
    {
        using var response = await pending;
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
