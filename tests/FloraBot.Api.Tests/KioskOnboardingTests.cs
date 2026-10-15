using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Notify;
using FloraBot.Api.Modules.Payment;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class KioskOnboardingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public KioskOnboardingTests(ApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientForRole(string role, Guid? userId = null, Guid? sellerId = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var token = _factory.Token(role, userId: userId, sellerId: sellerId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task PackageCatalog_ExposesActiveAnnualPackagesWithExactCapacities()
    {
        using var client = _factory.CreateClient();
        var packages = await client.GetFromJsonAsync<List<KioskPackageResponse>>("/api/kiosk-packages");
        Assert.NotNull(packages);
        Assert.True(packages.Count >= 3);

        var trial = packages.Single(x => x.Name == "Trial");
        Assert.Equal(10000, trial.AnnualFee);
        Assert.Equal(1, trial.WalkInSlots);
        Assert.Equal(1, trial.PreorderSlots);
        Assert.Equal(2, trial.TotalSlots);
        Assert.Equal(12, trial.TermMonths);

        var pro = packages.Single(x => x.Name == "Pro");
        Assert.Equal(15000, pro.AnnualFee);
        Assert.Equal(4, pro.WalkInSlots);
        Assert.Equal(4, pro.PreorderSlots);
        Assert.Equal(8, pro.TotalSlots);

        var max = packages.Single(x => x.Name == "Max");
        Assert.Equal(20000, max.AnnualFee);
        Assert.Equal(6, max.WalkInSlots);
        Assert.Equal(6, max.PreorderSlots);
        Assert.Equal(12, max.TotalSlots);
    }

    [Theory]
    [InlineData("https://payos.vn/return", "false", true)]
    [InlineData("http://localhost:3000/return", "true", true)]
    [InlineData("http://127.0.0.1:8080/cancel", "true", true)]
    [InlineData("http://[::1]:3000/return", "true", true)]
    [InlineData("http://example.com/return", "true", false)]
    [InlineData("http://192.168.1.1:3000/return", "true", false)]
    [InlineData("http://8.8.8.8:3000/return", "true", false)]
    [InlineData("https://user:pass@example.com/return", "true", false)]
    [InlineData("http://localhost:3000/return", "false", false)]
    public async Task PayOsClient_CallbackValidation_RejectsHttpNonLoopbackUserInfoAndEnforcesDevFlag(string callbackUrl, string devLoopbackFlag, bool expectedValid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PAYOS_CLIENT_ID"] = "test-client",
            ["PAYOS_API_KEY"] = "test-key",
            ["PAYOS_CHECKSUM_KEY"] = "test-checksum-key",
            ["PAYOS_RETURN_URL"] = callbackUrl,
            ["PAYOS_CANCEL_URL"] = callbackUrl,
            ["PAYOS_ALLOW_DEV_LOOPBACK"] = devLoopbackFlag
        }).Build();

        using var httpClient = new HttpClient(new MockPayOsHandler());
        var payOsClient = new PayOsClient(httpClient, config);

        if (expectedValid)
        {
            var res = await payOsClient.CreateAsync(12345, 10000, CancellationToken.None);
            Assert.NotNull(res.CheckoutUrl);
        }
        else
        {
            await Assert.ThrowsAsync<PaymentGatewayException>(() => payOsClient.CreateAsync(12345, 10000, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Customer_CanCreateApplication_AndEnforcesOneInFlightRule()
    {
        var customerId = Guid.NewGuid();
        using var client = CreateClientForRole("CUSTOMER", userId: customerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        // Ensure user exists in identity.users
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var insertUser = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Customer Test', '!hash', 'CUSTOMER', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertUser.Parameters.AddWithValue("id", customerId);
        insertUser.Parameters.AddWithValue("email", $"test-cust-{customerId:N}@example.invalid");
        await insertUser.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var appRequest = new CreateKioskApplicationRequest(
            trialPkg.Id,
            "Tiệm Hoa Tươi Tắn",
            "0901234567",
            "contact@tiemhoa.vn",
            "123 Nguyễn Trãi, Q5, TP.HCM",
            10.7551,
            106.6712,
            "Vietcombank",
            "LE THI C",
            "0071009876543"
        );

        var response = await client.PostAsJsonAsync("/api/member/kiosk-applications", appRequest);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<KioskApplicationResponse>();
        Assert.NotNull(created);
        Assert.Equal("AWAITING_PAYMENT", created.Status);
        Assert.Equal("Tiệm Hoa Tươi Tắn", created.ShopName);
        Assert.Equal("6543", created.BankAccountSuffix); // Masked suffix
        Assert.True(created.CanVoluntaryCancel);

        // Second creation for same customer should fail with 409 Conflict
        var secondResponse = await client.PostAsJsonAsync("/api/member/kiosk-applications", appRequest);
        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        // Fetch current application
        var currentResponse = await client.GetAsync("/api/member/kiosk-applications/current");
        currentResponse.EnsureSuccessStatusCode();
        var current = await currentResponse.Content.ReadFromJsonAsync<KioskApplicationResponse>();
        Assert.NotNull(current);
        Assert.Equal(created.Id, current.Id);
    }

    [Fact]
    public async Task Customer_CanVoluntaryCancel_UnpaidApplication()
    {
        var customerId = Guid.NewGuid();
        using var client = CreateClientForRole("CUSTOMER", userId: customerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertUser = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Cancel Customer', '!hash', 'CUSTOMER', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertUser.Parameters.AddWithValue("id", customerId);
        insertUser.Parameters.AddWithValue("email", $"cancel-cust-{customerId:N}@example.invalid");
        await insertUser.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var appRequest = new CreateKioskApplicationRequest(
            trialPkg.Id,
            "Shop Huỷ",
            "0909888777",
            "cancel@shop.vn",
            "456 Lê Duẩn, Q1",
            10.78,
            106.70,
            "Techcombank",
            "HOANG VAN D",
            "19030012345678"
        );

        var createRes = await client.PostAsJsonAsync("/api/member/kiosk-applications", appRequest);
        createRes.EnsureSuccessStatusCode();
        var created = (await createRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Cancel unpaid application
        var cancelRes = await client.PostAsJsonAsync($"/api/member/kiosk-applications/{created.Id}/cancel", new CancelKioskApplicationRequest("Đổi kế hoạch"));
        cancelRes.EnsureSuccessStatusCode();
        var cancelled = await cancelRes.Content.ReadFromJsonAsync<KioskApplicationResponse>();
        Assert.NotNull(cancelled);
        Assert.Equal("CANCELLED", cancelled.Status);
    }

    [Fact]
    public async Task LatePayment_AfterCancel_RoutesToRefundRequired_NeverReactivates()
    {
        var customerId = Guid.NewGuid();
        using var client = CreateClientForRole("CUSTOMER", userId: customerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertUser = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Late Customer', '!hash', 'CUSTOMER', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertUser.Parameters.AddWithValue("id", customerId);
        insertUser.Parameters.AddWithValue("email", $"late-cust-{customerId:N}@example.invalid");
        await insertUser.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var appRequest = new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Late Pay", "0911222333", "late@shop.vn", "789 Điện Biên Phủ", 10.79, 106.69, "MBBank", "VU THI E", "0900123456789"
        );

        var createRes = await client.PostAsJsonAsync("/api/member/kiosk-applications", appRequest);
        createRes.EnsureSuccessStatusCode();
        var created = (await createRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Cancel while unpaid
        var cancelRes = await client.PostAsJsonAsync($"/api/member/kiosk-applications/{created.Id}/cancel", new CancelKioskApplicationRequest("Hủy trước khi trả"));
        cancelRes.EnsureSuccessStatusCode();

        // Late payment arrives via flow.kiosk_application_paid
        await using var latePayment = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        latePayment.Parameters.AddWithValue("pid", created.PaymentId!.Value);
        latePayment.Parameters.AddWithValue("txn", $"PAYOS-LATE-{Guid.NewGuid():N}");
        await latePayment.ExecuteNonQueryAsync();

        // Verify status is REFUND_PENDING (NOT PAID_REVIEW and NOT ACTIVE)
        var updated = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal("REFUND_PENDING", updated.Status);

        // Verify audit log
        await using var checkAudit = source.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='LATE_PAYMENT_AFTER_CANCEL' AND entity_id=@id");
        checkAudit.Parameters.AddWithValue("id", created.Id);
        Assert.Equal(1L, await checkAudit.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ManagerContactConfirm_LocksVoluntaryCancellation()
    {
        var customerId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        using var custClient = CreateClientForRole("CUSTOMER", userId: customerId);
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@cid, @cemail, 'Confirm Cust', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@mid, @memail, 'Confirm Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("cid", customerId);
        insertUsers.Parameters.AddWithValue("cemail", $"confirm-cust-{customerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("memail", $"confirm-mgr-{managerId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var appRequest = new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Khoá Hủy", "0933444555", "lock@shop.vn", "100 CMT8, Q3", 10.77, 106.68, "ACB", "TRAN VAN F", "888899991111"
        );

        var createRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", appRequest);
        createRes.EnsureSuccessStatusCode();
        var created = (await createRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Simulate payment settled -> PAID_REVIEW
        await using var pay = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        pay.Parameters.AddWithValue("pid", created.PaymentId!.Value);
        pay.Parameters.AddWithValue("txn", $"PAYOS-LOCK-{Guid.NewGuid():N}");
        await pay.ExecuteNonQueryAsync();

        // Manager confirms contact
        var confirmRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{created.Id}/confirm", new ConfirmKioskApplicationRequest("Đã gọi điện xác nhận mặt bằng"));
        confirmRes.EnsureSuccessStatusCode();
        var confirmed = await confirmRes.Content.ReadFromJsonAsync<KioskApplicationResponse>();
        Assert.NotNull(confirmed);
        Assert.Equal("CONFIRMED", confirmed.Status);
        Assert.False(confirmed.CanVoluntaryCancel);

        // Customer attempts voluntary cancel after CONFIRMED -> 409 Conflict
        var cancelAttempt = await custClient.PostAsJsonAsync($"/api/member/kiosk-applications/{created.Id}/cancel", new CancelKioskApplicationRequest("Muốn hủy đổi ý"));
        Assert.Equal(HttpStatusCode.Conflict, cancelAttempt.StatusCode);
    }

    [Fact]
    public async Task Admin_IsDeniedOperationalTransitions()
    {
        var adminId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        await using var insertAdmin = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Admin Denied Ops', '!hash', 'ADMIN', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertAdmin.Parameters.AddWithValue("id", adminId);
        insertAdmin.Parameters.AddWithValue("email", $"admin-denied-{adminId:N}@example.invalid");
        await insertAdmin.ExecuteNonQueryAsync();

        using var adminClient = CreateClientForRole("ADMIN", userId: adminId);
        var appId = Guid.NewGuid();

        // Confirm endpoint denied for Admin
        var confirmRes = await adminClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{appId}/confirm", new ConfirmKioskApplicationRequest("Admin note"));
        Assert.Equal(HttpStatusCode.Forbidden, confirmRes.StatusCode);

        // Schedule endpoint denied for Admin
        var schedRes = await adminClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{appId}/schedule", new ScheduleKioskApplicationRequest(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "MORNING", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, schedRes.StatusCode);

        // Rework endpoint denied for Admin
        var reworkRes = await adminClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{appId}/rework", new ReworkKioskApplicationRequest("Admin rework"));
        Assert.Equal(HttpStatusCode.Forbidden, reworkRes.StatusCode);

        // Handover endpoint denied for Admin
        var handoverRes = await adminClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{appId}/handover", new { });
        Assert.Equal(HttpStatusCode.Forbidden, handoverRes.StatusCode);
    }

    [Fact]
    public async Task Handover_ExecutesAtomicActivation_PromotesToSeller_AssignsKioskAndYearSubscription()
    {
        var customerId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();
        using var custClient = CreateClientForRole("CUSTOMER", userId: customerId);
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var techClient = CreateClientForRole("TECHNICIAN", userId: techId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@cid, @cemail, 'Handover Customer', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@mid, @memail, 'Handover Ops Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE'),
              (@tid, @temail, 'Handover Tech', '!hash', 'TECHNICIAN', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("cid", customerId);
        insertUsers.Parameters.AddWithValue("cemail", $"ho-cust-{customerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("memail", $"ho-mgr-{managerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("tid", techId);
        insertUsers.Parameters.AddWithValue("temail", $"ho-tech-{techId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        // 1. Enroll a test kiosk with exact Trial slots (1 Walk-in, 1 Preorder)
        var enrollRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-TEST-{Guid.NewGuid():N}"[..12], "Kiosk Test Handover", $"ESP32-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        enrollRes.EnsureSuccessStatusCode();
        var enrolledKiosk = (await enrollRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        // 2. Create customer application
        var appRequest = new CreateKioskApplicationRequest(
            trialPkg.Id, "Hoa Lan Hoàng Gia", "0977888999", "lan@hoanggia.vn", "999 An Dương Vương, Q5", 10.75, 106.66, "VietinBank", "NGUYEN THI G", "1010099887766"
        );
        var createRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", appRequest);
        createRes.EnsureSuccessStatusCode();
        var created = (await createRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // 3. Settle payment -> PAID_REVIEW
        await using var pay = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        pay.Parameters.AddWithValue("pid", created.PaymentId!.Value);
        pay.Parameters.AddWithValue("txn", $"PAYOS-HO-{Guid.NewGuid():N}");
        await pay.ExecuteNonQueryAsync();

        // 4. Manager confirms contact -> CONFIRMED
        var confirmRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{created.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận thông tin thành công"));
        confirmRes.EnsureSuccessStatusCode();

        // 5. Manager schedules technician and kiosk -> SCHEDULED
        var schedRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{created.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "09:00 - 12:00", enrolledKiosk.Id
        ));
        schedRes.EnsureSuccessStatusCode();

        // 6. Technician starts visit -> INSTALLING
        var startRes = await techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{created.Id}/start", new { });
        startRes.EnsureSuccessStatusCode();

        // 7. Technician attaches photo evidence directly via flow helper
        await using var attachEvidence = source.CreateCommand("SELECT flow.attach_kiosk_evidence(@id, @appId, 'https://res.cloudinary.com/demo/image/upload/sample.jpg', 'image/jpeg', 1024, 'dummyhash', @actor)");
        attachEvidence.Parameters.AddWithValue("id", Guid.NewGuid());
        attachEvidence.Parameters.AddWithValue("appId", created.Id);
        attachEvidence.Parameters.AddWithValue("actor", techId);
        await attachEvidence.ExecuteNonQueryAsync();

        // 8. Technician submits installation report -> AWAITING_HANDOVER
        var submitRes = await techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{created.Id}/submit", new SubmitInstallationReportRequest("Đã lắp đặt hoàn tất khung tủ và cảm biến"));
        submitRes.EnsureSuccessStatusCode();

        // 9. Manager confirms handover -> ACTIVE
        var handoverRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{created.Id}/handover", new { });
        handoverRes.EnsureSuccessStatusCode();
        var activeApp = (await handoverRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;
        Assert.Equal("ACTIVE", activeApp.Status);
        Assert.NotNull(activeApp.SellerId);

        // 10. Verify DB assertions:
        // User promoted to SELLER
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == customerId);
        Assert.Equal("SELLER", user.Role);
        Assert.Equal(activeApp.SellerId, user.SellerId);

        // Kiosk address updated
        var kiosk = await db.Kiosks.AsNoTracking().SingleAsync(x => x.Id == enrolledKiosk.Id);
        Assert.Equal("999 An Dương Vương, Q5", kiosk.Address);

        // All slots assigned to the new seller
        var slots = await db.Slots.AsNoTracking().Where(x => x.KioskId == enrolledKiosk.Id).ToListAsync();
        Assert.All(slots, s => Assert.Equal(activeApp.SellerId, s.CurrentSellerId));

        // Active subscription for 1 calendar year created
        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.SellerId == activeApp.SellerId);
        Assert.Equal("ACTIVE", sub.Status);
        Assert.Equal(trialPkg.AnnualFee, sub.Price);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), sub.PeriodTo);
    }

    [Fact]
    public async Task AdminRefund_RequiresTwoAdminSeparationOfDuty_AndProofAttachment()
    {
        var admin1 = Guid.NewGuid();
        var admin2 = Guid.NewGuid();
        using var client1 = CreateClientForRole("ADMIN", userId: admin1);
        using var client2 = CreateClientForRole("ADMIN", userId: admin2);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertAdmins = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@a1, @e1, 'Admin Approver', '!hash', 'ADMIN', 'ACTIVE'),
              (@a2, @e2, 'Admin Payer', '!hash', 'ADMIN', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertAdmins.Parameters.AddWithValue("a1", admin1);
        insertAdmins.Parameters.AddWithValue("e1", $"admin1-{admin1:N}@example.invalid");
        insertAdmins.Parameters.AddWithValue("a2", admin2);
        insertAdmins.Parameters.AddWithValue("e2", $"admin2-{admin2:N}@example.invalid");
        await insertAdmins.ExecuteNonQueryAsync();

        var customerId = Guid.NewGuid();
        await using var insertCust = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Refund Cust', '!hash', 'CUSTOMER', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertCust.Parameters.AddWithValue("id", customerId);
        insertCust.Parameters.AddWithValue("email", $"refund-cust-{customerId:N}@example.invalid");
        await insertCust.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
        var encAccount = protection.CreateProtector("bank-account-v1").Protect("0071001234567");

        // Create application and move to REFUND_PENDING
        var appId = Guid.NewGuid();
        var payId = Guid.NewGuid();
        await using var insertApp = source.CreateCommand("""
            INSERT INTO payment.payments(id, kind, purpose, amount, gateway, idempotency_key, status, paid_at)
            VALUES(@pid, 'CHARGE', 'KIOSK_APPLICATION', 10000, 'PAYOS', 'kiosk-refund-test-' || @aid::text, 'SUCCEEDED', public.app_now());

            INSERT INTO kiosk_ops.kiosk_applications(id, customer_id, package_id, shop_name, phone, contact_email, installation_address, latitude, longitude, bank_name, bank_holder, bank_account_no_enc, status, payment_id, package_name, annual_fee, walk_in_slots, preorder_slots, total_slots)
            VALUES(@aid, @cid, @pkg, 'Shop Refund Test', '0912345678', 'rf@test.vn', 'Address', 10.7, 106.6, 'VCB', 'CHỦ TK', @enc, 'REFUND_PENDING', @pid, 'Trial', 10000, 1, 1, 2);

            INSERT INTO payment.payments(id, kind, purpose, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
            VALUES(gen_random_uuid(), 'REFUND', 'KIOSK_APPLICATION', @pid, 'MANUAL', 'ref-app-' || @pid::text, 10000, 'PENDING', flow.sys_user(), 'TEST_REFUND');
            """);
        insertApp.Parameters.AddWithValue("pid", payId);
        insertApp.Parameters.AddWithValue("aid", appId);
        insertApp.Parameters.AddWithValue("cid", customerId);
        insertApp.Parameters.AddWithValue("pkg", trialPkg.Id);
        insertApp.Parameters.AddWithValue("enc", encAccount);
        await insertApp.ExecuteNonQueryAsync();

        // 1. Admin 1 approves refund
        var approveRes = await client1.PostAsJsonAsync($"/api/admin/kiosk-applications/{appId}/refund/approve", new { });
        approveRes.EnsureSuccessStatusCode();

        // 1b. Admin 1 (approver) attempts to reveal bank details -> 409 Conflict
        var revealAttempt = await client1.PostAsJsonAsync($"/api/admin/kiosk-applications/{appId}/refund/bank-details", new { });
        Assert.Equal(HttpStatusCode.Conflict, revealAttempt.StatusCode);

        // 1c. Admin 2 (payer) reveals bank details -> 200 OK
        var revealRes = await client2.PostAsJsonAsync($"/api/admin/kiosk-applications/{appId}/refund/bank-details", new { });
        revealRes.EnsureSuccessStatusCode();
        var revealedBank = await revealRes.Content.ReadFromJsonAsync<KioskRefundBankResponse>();
        Assert.NotNull(revealedBank);
        Assert.Equal("VCB", revealedBank.BankName);

        // 2. Admin 1 attempts to confirm payout -> 409 Conflict (separation of duty)
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        var url1 = $"https://proof.example/{Guid.NewGuid():N}.jpg";
        var ticket1 = tickets.Issue("confirm_refund", PrivateEvidence.AdminBinding(admin1.ToString(), appId),
            new VerifiedMedia(url1, "proof1", "image/jpeg", 1024, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

        var refTxn = $"VCB-REF-{Guid.NewGuid():N}"[..20];
        var sameAdminAttempt = await client1.PostAsJsonAsync($"/api/admin/kiosk-applications/{appId}/refund/confirm", new ConfirmKioskRefundRequest(refTxn, ticket1.Reference));
        Assert.Equal(HttpStatusCode.Conflict, sameAdminAttempt.StatusCode);

        // 3. Admin 2 confirms payout with proof -> 200 OK
        var url2 = $"https://proof.example/{Guid.NewGuid():N}.jpg";
        var ticket2 = tickets.Issue("confirm_refund", PrivateEvidence.AdminBinding(admin2.ToString(), appId),
            new VerifiedMedia(url2, "proof2", "image/jpeg", 1024, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

        var confirmRes = await client2.PostAsJsonAsync($"/api/admin/kiosk-applications/{appId}/refund/confirm", new ConfirmKioskRefundRequest(refTxn, ticket2.Reference));
        confirmRes.EnsureSuccessStatusCode();

        var refundedApp = await db.KioskApplications.AsNoTracking().SingleAsync(x => x.Id == appId);
        Assert.Equal("REFUNDED", refundedApp.Status);
        Assert.Equal(admin2, refundedApp.RefundPaidBy);
        Assert.Equal(refTxn, refundedApp.RefundReference);
    }

    [Fact]
    public async Task RawJsonCoordinates_Validation_RejectsMissingSingleNullAndOutOfRange_AllowsZeroZero()
    {
        var customerId = Guid.NewGuid();
        using var client = CreateClientForRole("CUSTOMER", userId: customerId);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertUser = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Coord Customer', '!hash', 'CUSTOMER', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertUser.Parameters.AddWithValue("id", customerId);
        insertUser.Parameters.AddWithValue("email", $"coord-{customerId:N}@example.invalid");
        await insertUser.ExecuteNonQueryAsync();

        var trialPkgId = Guid.Parse("20000000-0000-0000-0000-00000000000c");

        // 1. Missing both coordinates
        var jsonNoCoords = JsonSerializer.Serialize(new
        {
            packageId = trialPkgId,
            shopName = "No Coords",
            phone = "0901234567",
            contactEmail = "nc@shop.vn",
            installationAddress = "Address",
            bankName = "VCB",
            bankHolder = "HOLDER",
            bankAccountNumber = "123456"
        });
        var res1 = await client.PostAsync("/api/member/kiosk-applications", new StringContent(jsonNoCoords, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res1.StatusCode);

        // 2. Single coordinate (latitude only)
        var jsonLatOnly = JsonSerializer.Serialize(new
        {
            packageId = trialPkgId,
            shopName = "Lat Only",
            phone = "0901234567",
            contactEmail = "lo@shop.vn",
            installationAddress = "Address",
            latitude = 10.7,
            bankName = "VCB",
            bankHolder = "HOLDER",
            bankAccountNumber = "123456"
        });
        var res2 = await client.PostAsync("/api/member/kiosk-applications", new StringContent(jsonLatOnly, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);

        // 3. Null latitude
        var jsonNullLat = JsonSerializer.Serialize(new
        {
            packageId = trialPkgId,
            shopName = "Null Lat",
            phone = "0901234567",
            contactEmail = "nl@shop.vn",
            installationAddress = "Address",
            latitude = (double?)null,
            longitude = 106.7,
            bankName = "VCB",
            bankHolder = "HOLDER",
            bankAccountNumber = "123456"
        });
        var res3 = await client.PostAsync("/api/member/kiosk-applications", new StringContent(jsonNullLat, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res3.StatusCode);

        // 4. Out of range latitude (95.0)
        var jsonOutLat = JsonSerializer.Serialize(new
        {
            packageId = trialPkgId,
            shopName = "Out Lat",
            phone = "0901234567",
            contactEmail = "out@shop.vn",
            installationAddress = "Address",
            latitude = 95.0,
            longitude = 106.7,
            bankName = "VCB",
            bankHolder = "HOLDER",
            bankAccountNumber = "123456"
        });
        var res4 = await client.PostAsync("/api/member/kiosk-applications", new StringContent(jsonOutLat, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res4.StatusCode);

        // 5. Explicit 0.0, 0.0 coordinates -> 201 Created
        var jsonZeroZero = JsonSerializer.Serialize(new
        {
            packageId = trialPkgId,
            shopName = "Zero Zero Shop",
            phone = "0901234567",
            contactEmail = "zero@shop.vn",
            installationAddress = "Equator Meridian",
            latitude = 0.0,
            longitude = 0.0,
            bankName = "VCB",
            bankHolder = "HOLDER",
            bankAccountNumber = "123456"
        });
        var res5 = await client.PostAsync("/api/member/kiosk-applications", new StringContent(jsonZeroZero, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res5.StatusCode);
    }

    [Fact]
    public async Task ImmutablePackageSnapshot_PreservesPurchasedTerms_WhenCatalogMutatesLater()
    {
        var customerId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        using var custClient = CreateClientForRole("CUSTOMER", userId: customerId);
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@cid, @ce, 'Snap Customer', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@mid, @me, 'Snap Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("cid", customerId);
        insertUsers.Parameters.AddWithValue("ce", $"snap-c-{customerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("me", $"snap-m-{managerId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var appRequest = new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Snapshot Test", "0901112233", "snap@test.vn", "123 Snap St", 10.5, 106.5, "VCB", "CHỦ", "11223344"
        );
        var createRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", appRequest);
        createRes.EnsureSuccessStatusCode();
        var created = (await createRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        Assert.Equal(10000, created.AnnualFee);
        Assert.Equal(2, created.TotalSlots);
        Assert.Equal("Trial", created.PackageName);

        // Mutate catalog package in DB: price 999999, max_slots 50
        await using var mutatePkg = source.CreateCommand("UPDATE identity.subscription_packages SET annual_fee=999999, max_slots=50 WHERE id=@id");
        mutatePkg.Parameters.AddWithValue("id", trialPkg.Id);
        await mutatePkg.ExecuteNonQueryAsync();

        try
        {
            // Verify current customer endpoint returns original snapshot
            var curRes = await custClient.GetAsync("/api/member/kiosk-applications/current");
            curRes.EnsureSuccessStatusCode();
            var curApp = await curRes.Content.ReadFromJsonAsync<KioskApplicationResponse>();
            Assert.NotNull(curApp);
            Assert.Equal(10000, curApp.AnnualFee);
            Assert.Equal(2, curApp.TotalSlots);
            Assert.Equal("Trial", curApp.PackageName);

            // Verify manager list returns original snapshot
            var mgrListRes = await mgrClient.GetAsync($"/api/operations/kiosk-applications?status=AWAITING_PAYMENT");
            mgrListRes.EnsureSuccessStatusCode();
            var page = await mgrListRes.Content.ReadFromJsonAsync<KioskApplicationPage>();
            Assert.NotNull(page);
            var item = page.Items.Single(x => x.Id == created.Id);
            Assert.Equal(10000, item.AnnualFee);
            Assert.Equal(2, item.TotalSlots);
        }
        finally
        {
            // Restore catalog package
            await using var restorePkg = source.CreateCommand("UPDATE identity.subscription_packages SET annual_fee=10000, max_slots=2 WHERE id=@id");
            restorePkg.Parameters.AddWithValue("id", trialPkg.Id);
            await restorePkg.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Schedule_RejectsOverlappingTechnicianJobs_AndConcurrentDoubleAllocation()
    {
        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();
        var cust1 = Guid.NewGuid();
        var cust2 = Guid.NewGuid();
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var custClient1 = CreateClientForRole("CUSTOMER", userId: cust1);
        using var custClient2 = CreateClientForRole("CUSTOMER", userId: cust2);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@mid, @me, 'Sched Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE'),
              (@tid, @te, 'Sched Tech', '!hash', 'TECHNICIAN', 'ACTIVE'),
              (@c1, @ce1, 'Sched Cust 1', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@c2, @ce2, 'Sched Cust 2', '!hash', 'CUSTOMER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("me", $"smgr-{managerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("tid", techId);
        insertUsers.Parameters.AddWithValue("te", $"stech-{techId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("c1", cust1);
        insertUsers.Parameters.AddWithValue("ce1", $"scust1-{cust1:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("c2", cust2);
        insertUsers.Parameters.AddWithValue("ce2", $"scust2-{cust2:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        // Enroll two kiosks with 1W + 1P
        var k1Res = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-SCH1-{Guid.NewGuid():N}"[..12], "Kiosk Sched 1", $"ESP-S1-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        var kiosk1 = (await k1Res.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        var k2Res = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-SCH2-{Guid.NewGuid():N}"[..12], "Kiosk Sched 2", $"ESP-S2-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        var kiosk2 = (await k2Res.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        // Create two applications and confirm them
        var app1Res = await custClient1.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Sched 1", "0901234111", "s1@t.vn", "Add 1", 10.1, 106.1, "VCB", "A", "111111"
        ));
        var app1 = (await app1Res.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;
        await using var p1 = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        p1.Parameters.AddWithValue("pid", app1.PaymentId!.Value);
        p1.Parameters.AddWithValue("txn", $"PAYOS-S1-{Guid.NewGuid():N}");
        await p1.ExecuteNonQueryAsync();
        var c1 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app1.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận thông tin 1"));
        c1.EnsureSuccessStatusCode();

        var app2Res = await custClient2.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Sched 2", "0901234222", "s2@t.vn", "Add 2", 10.2, 106.2, "VCB", "B", "222222"
        ));
        var app2 = (await app2Res.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;
        await using var p2 = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        p2.Parameters.AddWithValue("pid", app2.PaymentId!.Value);
        p2.Parameters.AddWithValue("txn", $"PAYOS-S2-{Guid.NewGuid():N}");
        await p2.ExecuteNonQueryAsync();
        var c2 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app2.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận thông tin 2"));
        c2.EnsureSuccessStatusCode();

        var scheduledDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3));
        var timeWindow = "09:00 - 12:00";

        // 1. Schedule App 1 with Tech A on Kiosk 1 -> succeeds
        var s1Res = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app1.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, scheduledDate, timeWindow, kiosk1.Id
        ));
        s1Res.EnsureSuccessStatusCode();

        // 2. Schedule App 2 with same Tech A at the same date and time window -> 409 Conflict
        var overlapTech = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app2.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, scheduledDate, timeWindow, kiosk2.Id
        ));
        Assert.Equal(HttpStatusCode.Conflict, overlapTech.StatusCode);

        // 3. Schedule App 2 for Kiosk 1 (already reserved by App 1) -> 409 Conflict
        var anotherTech = Guid.NewGuid();
        await using var insertTech2 = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Tech 2', '!hash', 'TECHNICIAN', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertTech2.Parameters.AddWithValue("id", anotherTech);
        insertTech2.Parameters.AddWithValue("email", $"t2-{anotherTech:N}@example.invalid");
        await insertTech2.ExecuteNonQueryAsync();

        var doubleKiosk = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app2.Id}/schedule", new ScheduleKioskApplicationRequest(
            anotherTech, scheduledDate, "14:00 - 17:00", kiosk1.Id
        ));
        Assert.Equal(HttpStatusCode.Conflict, doubleKiosk.StatusCode);
    }

    [Fact]
    public async Task Kiosk_EnrollmentAndKeyRotation_ReturnsRawKeyOnceOnly_NoStore_FindsByHash()
    {
        var managerId = Guid.NewGuid();
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertMgr = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Key Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertMgr.Parameters.AddWithValue("id", managerId);
        insertMgr.Parameters.AddWithValue("email", $"kmgr-{managerId:N}@example.invalid");
        await insertMgr.ExecuteNonQueryAsync();

        // Enroll kiosk
        var enrollRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-KEY-{Guid.NewGuid():N}"[..12], "Kiosk Key Test", $"ESP-K-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        enrollRes.EnsureSuccessStatusCode();
        Assert.True(enrollRes.Headers.CacheControl?.NoStore);
        var enrolled = (await enrollRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;
        Assert.NotNull(enrolled.ApiKey);
        Assert.Equal(64, enrolled.ApiKey.Length);

        // Verify DeviceCredentials finds kiosk by SHA256 of the returned raw key
        var keyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(enrolled.ApiKey)));
        var foundId = await DeviceCredentials.FindEnabledAsync(db, keyHash, CancellationToken.None);
        Assert.Equal(enrolled.Id, foundId);

        // Rotate key
        var rotateRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosks/{enrolled.Id}/provision-key", new { });
        rotateRes.EnsureSuccessStatusCode();
        Assert.True(rotateRes.Headers.CacheControl?.NoStore);
        var rotated = (await rotateRes.Content.ReadFromJsonAsync<ProvisionKioskKeyResponse>())!;
        Assert.NotNull(rotated.ApiKey);
        Assert.NotEqual(enrolled.ApiKey, rotated.ApiKey);

        // New hash finds kiosk; old hash does not
        var newHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rotated.ApiKey)));
        var foundNew = await DeviceCredentials.FindEnabledAsync(db, newHash, CancellationToken.None);
        Assert.Equal(enrolled.Id, foundNew);

        var foundOld = await DeviceCredentials.FindEnabledAsync(db, keyHash, CancellationToken.None);
        Assert.Null(foundOld);
    }

    [Fact]
    public async Task LegacyMonthlyPackages_ExcludeAnnualTiers_AndSubscribeRejectsAnnualTier()
    {
        using var client = _factory.CreateClient();
        var packages = await client.GetFromJsonAsync<List<PackageResponse>>("/api/packages");
        Assert.NotNull(packages);
        // GET /api/packages must only return monthly packages, never annual tiers with monthlyFee = 0
        Assert.All(packages, p => Assert.True(p.MonthlyFee > 0));
        Assert.DoesNotContain(packages, p => p.Name is "Trial" or "Pro" or "Max");

        // Verify flow.subscribe rejects annual package
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var sellerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var trialPkgId = Guid.Parse("20000000-0000-0000-0000-00000000000c");

        await using var insertSeller = source.CreateCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, address, status, package_id, package_expires_at)
            VALUES(@sid, 'Annual Seller', '0901234567', 'HCM', 'ACTIVE', @pkg, CURRENT_DATE + interval '1 year');
            INSERT INTO identity.users(id, email, full_name, password_hash, role, seller_id, status)
            VALUES(@uid, @ue, 'Annual Owner', '!hash', 'SELLER', @sid, 'ACTIVE');
            """);
        insertSeller.Parameters.AddWithValue("sid", sellerId);
        insertSeller.Parameters.AddWithValue("pkg", trialPkgId);
        insertSeller.Parameters.AddWithValue("uid", userId);
        insertSeller.Parameters.AddWithValue("ue", $"aseller-{sellerId:N}@example.invalid");
        await insertSeller.ExecuteNonQueryAsync();

        await using var subCmd = source.CreateCommand("SELECT flow.subscribe(@sid, @uid, 1)");
        subCmd.Parameters.AddWithValue("sid", sellerId);
        subCmd.Parameters.AddWithValue("uid", userId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => subCmd.ExecuteScalarAsync());
        Assert.Equal("P0001", ex.SqlState);
        Assert.Contains("không thể gia hạn theo tháng", ex.MessageText);
    }

    [Fact]
    public async Task Evidence_PrivateBinaryFetch_EnforcesRoleAndTenantAccess_RejectsTamperedTicket()
    {
        var managerId = Guid.NewGuid();
        var tech1 = Guid.NewGuid();
        var tech2 = Guid.NewGuid();
        var custId = Guid.NewGuid();
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var tech1Client = CreateClientForRole("TECHNICIAN", userId: tech1);
        using var tech2Client = CreateClientForRole("TECHNICIAN", userId: tech2);
        using var custClient = CreateClientForRole("CUSTOMER", userId: custId);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@mid, @me, 'Ev Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE'),
              (@t1, @te1, 'Ev Tech 1', '!hash', 'TECHNICIAN', 'ACTIVE'),
              (@t2, @te2, 'Ev Tech 2', '!hash', 'TECHNICIAN', 'ACTIVE'),
              (@cid, @ce, 'Ev Cust', '!hash', 'CUSTOMER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("me", $"evmgr-{managerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("t1", tech1);
        insertUsers.Parameters.AddWithValue("te1", $"evtech1-{tech1:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("t2", tech2);
        insertUsers.Parameters.AddWithValue("te2", $"evtech2-{tech2:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("cid", custId);
        insertUsers.Parameters.AddWithValue("ce", $"evcust-{custId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        // Enroll kiosk
        var kRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-EV-{Guid.NewGuid():N}"[..12], "Kiosk Evidence Test", $"ESP-EV-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        var kiosk = (await kRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        // Customer creates application
        var appRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Ev", "0901234567", "ev@test.vn", "123 Ev Rd", 10.5, 106.5, "VCB", "CHỦ", "112233"
        ));
        var app = (await appRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Pay and confirm
        await using var p = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        p.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        p.Parameters.AddWithValue("txn", $"PAYOS-EV-{Guid.NewGuid():N}");
        await p.ExecuteNonQueryAsync();

        var cRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận thông tin hợp lệ"));
        cRes.EnsureSuccessStatusCode();

        // Schedule tech 1
        var sRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/schedule", new ScheduleKioskApplicationRequest(
            tech1, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)), "09:00 - 12:00", kiosk.Id
        ));
        sRes.EnsureSuccessStatusCode();

        // Tech 1 starts visit
        var startRes = await tech1Client.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/start", new { });
        startRes.EnsureSuccessStatusCode();

        // Attempt to attach tampered evidence ticket -> 400 Bad Request
        var tamperedAttach = await tech1Client.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/evidence", new AttachKioskEvidenceRequest(Guid.NewGuid(), "evidence:v1:tampered-ticket"));
        Assert.Equal(HttpStatusCode.BadRequest, tamperedAttach.StatusCode);

        // Upload valid evidence using ticket with unique URL
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        var uniqueUrl = $"https://res.cloudinary.com/demo/image/upload/sample-{Guid.NewGuid():N}.jpg";
        var validTicket = tickets.Issue("kiosk_installation", $"tech-kiosk:{tech1}:{app.Id}",
            new VerifiedMedia(uniqueUrl, "sample", "image/jpeg", 1024, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"));

        var attId = Guid.NewGuid();
        var attachRes = await tech1Client.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/evidence", new AttachKioskEvidenceRequest(attId, validTicket.Reference));
        attachRes.EnsureSuccessStatusCode();

        // Foreign tech 2 attempts to fetch evidence -> 404 Not Found
        var foreignFetch = await tech2Client.GetAsync($"/api/staff/kiosk-installations/{app.Id}/evidence/{attId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignFetch.StatusCode);

        // Foreign tech 2 attempts to submit -> 403 Forbidden
        var foreignSubmit = await tech2Client.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/submit", new SubmitInstallationReportRequest("Report from foreign"));
        Assert.Equal(HttpStatusCode.Forbidden, foreignSubmit.StatusCode);
    }

    [Fact]
    public async Task Reconcile_RealEndpoint_AllowsLatePaidAfterCancel_TransitionsToRefundPending()
    {
        var custId = Guid.NewGuid();
        using var custClient = CreateClientForRole("CUSTOMER", userId: custId);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertCust = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@cid, @ce, 'Reconcile Cust', '!hash', 'CUSTOMER', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertCust.Parameters.AddWithValue("cid", custId);
        insertCust.Parameters.AddWithValue("ce", $"reconcile-{custId:N}@example.invalid");
        await insertCust.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var appRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Reconcile Test", "0901234888", "rec@test.vn", "456 Rec Blvd", 10.6, 106.6, "VCB", "CHỦ", "889900"
        ));
        var app = (await appRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Reserve gateway order code
        await using var reserve = source.CreateCommand("SELECT flow.reserve_gateway_order(@pid)");
        reserve.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        var orderCode = (long)(await reserve.ExecuteScalarAsync())!;

        // Cancel while unpaid -> CANCELLED
        var cancelRes = await custClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/cancel", new CancelKioskApplicationRequest("Hủy"));
        cancelRes.EnsureSuccessStatusCode();

        // Create client with mocked PayOsClient that returns PAID status for this orderCode
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PAYOS_CLIENT_ID", "test-client");
            builder.UseSetting("PAYOS_API_KEY", "test-key");
            builder.UseSetting("PAYOS_RETURN_URL", "https://payos.vn/return");
            builder.UseSetting("PAYOS_CANCEL_URL", "https://payos.vn/cancel");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<PayOsClient>().ConfigurePrimaryHttpMessageHandler(() => new MockReconcileHandler(orderCode, 10000, "PAID"));
            });
        });

        var token = _factory.Token("CUSTOMER", userId: custId);
        using var customClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        customClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Call real HTTP reconcile endpoint
        var recRes = await customClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/reconcile", new { });
        recRes.EnsureSuccessStatusCode();
        var recApp = await recRes.Content.ReadFromJsonAsync<KioskApplicationResponse>();
        Assert.NotNull(recApp);

        // Assert transitions to REFUND_PENDING (late payment after cancellation)
        Assert.Equal("REFUND_PENDING", recApp.Status);

        // Verify payment is marked SUCCEEDED
        await using var checkPay = source.CreateCommand("SELECT status FROM payment.payments WHERE id=@pid");
        checkPay.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        Assert.Equal("SUCCEEDED", await checkPay.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Lifecycle_HandoverCreatesActiveSlotAssignments_RentedEmpty_AndEnforcesZoneguards()
    {
        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var techClient = CreateClientForRole("TECHNICIAN", userId: techId);
        using var custClient = CreateClientForRole("CUSTOMER", userId: customerId);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@mid, @me, 'Zone Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE'),
              (@tid, @te, 'Zone Tech', '!hash', 'TECHNICIAN', 'ACTIVE'),
              (@cid, @ce, 'Zone Cust', '!hash', 'CUSTOMER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("me", $"zmgr-{managerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("tid", techId);
        insertUsers.Parameters.AddWithValue("te", $"ztech-{techId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("cid", customerId);
        insertUsers.Parameters.AddWithValue("ce", $"zcust-{customerId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        // Enroll kiosk with 1 WALK_IN + 1 PREORDER slot
        var enrollRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-ZN-{Guid.NewGuid():N}"[..12], "Kiosk Zone Guard", $"ESP-ZN-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        enrollRes.EnsureSuccessStatusCode();
        var kiosk = (await enrollRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        // Customer creates application
        var appRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Tiệm Hoa Zone Guard", "0901239999", "zone@test.vn", "777 Zone Street, Q1", 10.77, 106.69, "VCB", "CHỦ TIỆM", "99887766"
        ));
        appRes.EnsureSuccessStatusCode();
        var app = (await appRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Payment settle to PAID_REVIEW
        await using var pCmd = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        pCmd.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        pCmd.Parameters.AddWithValue("txn", $"PAYOS-ZN-{Guid.NewGuid():N}");
        await pCmd.ExecuteNonQueryAsync();

        // Confirm
        var confRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận thông tin"));
        confRes.EnsureSuccessStatusCode();

        // Schedule
        var schedRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "09:00 - 12:00", kiosk.Id
        ));
        schedRes.EnsureSuccessStatusCode();

        // Start
        var startRes = await techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/start", new { });
        startRes.EnsureSuccessStatusCode();

        // Attach evidence
        await using var attach = source.CreateCommand("SELECT flow.attach_kiosk_evidence(@id, @appId, 'https://res.cloudinary.com/demo/image/upload/sample.jpg', 'image/jpeg', 1024, 'dummyhash', @actor)");
        attach.Parameters.AddWithValue("id", Guid.NewGuid());
        attach.Parameters.AddWithValue("appId", app.Id);
        attach.Parameters.AddWithValue("actor", techId);
        await attach.ExecuteNonQueryAsync();

        // Submit report
        var subRes = await techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/submit", new SubmitInstallationReportRequest("Hoàn thành lắp đặt đầy đủ thiết bị và kiểm tra nguồn"));
        subRes.EnsureSuccessStatusCode();

        // Handover
        var handRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/handover", new { });
        handRes.EnsureSuccessStatusCode();
        var handedApp = (await handRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;
        Assert.Equal("ACTIVE", handedApp.Status);
        Assert.NotNull(handedApp.SellerId);
        var sellerId = handedApp.SellerId.Value;

        // 1. Verify all slots of the kiosk are RENTED_EMPTY and have ACTIVE slot_assignments
        var slots = await db.Slots.AsNoTracking().Where(x => x.KioskId == kiosk.Id).ToListAsync();
        Assert.Equal(2, slots.Count);
        Assert.All(slots, s =>
        {
            Assert.Equal("RENTED_EMPTY", s.Status);
            Assert.Equal(sellerId, s.CurrentSellerId);
        });

        await using var checkAssign = source.CreateCommand("SELECT count(*) FROM kiosk_ops.slot_assignments WHERE seller_id=@sid AND status='ACTIVE'");
        checkAssign.Parameters.AddWithValue("sid", sellerId);
        Assert.Equal(2L, await checkAssign.ExecuteScalarAsync());

        // Verify flow.slot_sellable returns true for both slots
        var slotW = slots.Single(s => s.Zone == "WALK_IN").Id;
        var slotP = slots.Single(s => s.Zone == "PREORDER").Id;

        await using var checkSellableW = source.CreateCommand("SELECT flow.slot_sellable(@slot, @seller)");
        checkSellableW.Parameters.AddWithValue("slot", slotW);
        checkSellableW.Parameters.AddWithValue("seller", sellerId);
        Assert.True((bool)(await checkSellableW.ExecuteScalarAsync())!);

        await using var checkSellableP = source.CreateCommand("SELECT flow.slot_sellable(@slot, @seller)");
        checkSellableP.Parameters.AddWithValue("slot", slotP);
        checkSellableP.Parameters.AddWithValue("seller", sellerId);
        Assert.True((bool)(await checkSellableP.ExecuteScalarAsync())!);

        // 2. Create Artificial Product for Seller
        var productId = Guid.NewGuid();
        await using var insertProd = source.CreateCommand("""
            INSERT INTO catalog.flower_products(id, seller_id, name, description, price, tags, shelf_life_hours, status)
            VALUES(@pid, @sid, 'Hoa Lụa Cao Cấp', 'Bền đẹp mãi mãi', 50000, ARRAY['ROMANTIC','GIFT'], 720, 'ACTIVE');
            """);
        insertProd.Parameters.AddWithValue("pid", productId);
        insertProd.Parameters.AddWithValue("sid", sellerId);
        await insertProd.ExecuteNonQueryAsync();

        // Stock bouquet into WALK_IN slot (via flow.stock_bouquet)
        var batchW = Guid.NewGuid();
        var qrW = $"QR-W-{Guid.NewGuid():N}"[..20];
        await using var stockWCmd = source.CreateCommand("SELECT flow.stock_bouquet(@batch, @prod, @slot, @actor, @qr)");
        stockWCmd.Parameters.AddWithValue("batch", batchW);
        stockWCmd.Parameters.AddWithValue("prod", productId);
        stockWCmd.Parameters.AddWithValue("slot", slotW);
        stockWCmd.Parameters.AddWithValue("actor", customerId);
        stockWCmd.Parameters.AddWithValue("qr", qrW);
        var bkW = (Guid)(await stockWCmd.ExecuteScalarAsync())!;

        // Stock bouquet into PREORDER slot
        var batchP = Guid.NewGuid();
        var qrP = $"QR-P-{Guid.NewGuid():N}"[..20];
        await using var stockPCmd = source.CreateCommand("SELECT flow.stock_bouquet(@batch, @prod, @slot, @actor, @qr)");
        stockPCmd.Parameters.AddWithValue("batch", batchP);
        stockPCmd.Parameters.AddWithValue("prod", productId);
        stockPCmd.Parameters.AddWithValue("slot", slotP);
        stockPCmd.Parameters.AddWithValue("actor", customerId);
        stockPCmd.Parameters.AddWithValue("qr", qrP);
        var bkP = (Guid)(await stockPCmd.ExecuteScalarAsync())!;

        // 3. Zoneguard Verification:
        // WALK_IN bouquet bkW appears on screen.v_kiosk_catalog, PREORDER bouquet bkP does NOT
        await using var checkKioskCat = source.CreateCommand("SELECT bouquet_id FROM screen.v_kiosk_catalog WHERE kiosk_id=@kid");
        checkKioskCat.Parameters.AddWithValue("kid", kiosk.Id);
        var kioskCatBouquets = new List<Guid>();
        await using (var r = await checkKioskCat.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) kioskCatBouquets.Add(r.GetGuid(0));
        }
        Assert.Contains(bkW, kioskCatBouquets);
        Assert.DoesNotContain(bkP, kioskCatBouquets);

        // PREORDER bouquet bkP appears in screen.v_web_catalog, WALK_IN bouquet bkW does NOT
        await using var checkWebCat = source.CreateCommand("SELECT unnest(bouquets) FROM screen.v_web_catalog WHERE kiosk_id=@kid");
        checkWebCat.Parameters.AddWithValue("kid", kiosk.Id);
        var webCatBouquets = new List<Guid>();
        await using (var r = await checkWebCat.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) webCatBouquets.Add(r.GetGuid(0));
        }
        Assert.Contains(bkP, webCatBouquets);
        Assert.DoesNotContain(bkW, webCatBouquets);

        // Make kiosk ONLINE for checkout test
        await using var makeOnline = source.CreateCommand("UPDATE kiosk_ops.kiosks SET status='ONLINE', last_heartbeat_at=public.app_now() WHERE id=@kid");
        makeOnline.Parameters.AddWithValue("kid", kiosk.Id);
        await makeOnline.ExecuteNonQueryAsync();

        // 4. Walk-in kiosk_checkout for bkW (WALK_IN) succeeds
        await using var coW = source.CreateCommand("SELECT flow.kiosk_checkout(@kid, ARRAY[@bq]::uuid[], @cid)");
        coW.Parameters.AddWithValue("kid", kiosk.Id);
        coW.Parameters.AddWithValue("bq", bkW);
        coW.Parameters.AddWithValue("cid", customerId);
        var coWId = (Guid)(await coW.ExecuteScalarAsync())!;
        Assert.NotEqual(Guid.Empty, coWId);

        // Direct walk-in kiosk_checkout for bkP (PREORDER) FAILS with zone error
        await using var coP = source.CreateCommand("SELECT flow.kiosk_checkout(@kid, ARRAY[@bq]::uuid[], @cid)");
        coP.Parameters.AddWithValue("kid", kiosk.Id);
        coP.Parameters.AddWithValue("bq", bkP);
        coP.Parameters.AddWithValue("cid", customerId);
        var exP = await Assert.ThrowsAsync<PostgresException>(() => coP.ExecuteScalarAsync());
        Assert.Contains("PREORDER", exP.MessageText);

        // 5. Web preorder flow.web_request on WALK_IN slot bouquet FAILS
        await using var webReqW = source.CreateCommand("SELECT flow.web_request(@id, @cid, 'STOCK', @pid, @kid, @bq, public.app_now()+interval '1 day', 'Huong dan')");
        webReqW.Parameters.AddWithValue("id", Guid.NewGuid());
        webReqW.Parameters.AddWithValue("cid", customerId);
        webReqW.Parameters.AddWithValue("pid", productId);
        webReqW.Parameters.AddWithValue("kid", kiosk.Id);
        webReqW.Parameters.AddWithValue("bq", bkW);
        var exWebW = await Assert.ThrowsAsync<PostgresException>(() => webReqW.ExecuteScalarAsync());
        Assert.Contains("WALK_IN", exWebW.MessageText);

        // 6. Custom preorder fulfillment via flow.web_fulfill on WALK_IN slot FAILS
        var customReqId = Guid.NewGuid();
        var fakeOrderId = Guid.NewGuid();
        await using var createCustom = source.CreateCommand("SELECT flow.web_request(@id, @cid, 'CUSTOM', @pid, @kid, NULL, public.app_now()+interval '3 hours', 'Dat rieng 10 ky tu it nhat')");
        createCustom.Parameters.AddWithValue("id", customReqId);
        createCustom.Parameters.AddWithValue("cid", customerId);
        createCustom.Parameters.AddWithValue("pid", productId);
        createCustom.Parameters.AddWithValue("kid", kiosk.Id);
        await createCustom.ExecuteNonQueryAsync();

        await using var createOrder = source.CreateCommand("""
            INSERT INTO ordering.orders(id, order_code, checkout_id, customer_id, seller_id, kiosk_id, status, subtotal, total_amount, tracking_token)
            VALUES(@oid, flow.new_order_code(), gen_random_uuid(), @cid, @sid, @kid, 'PAID', 50000, 50000, flow.new_tracking_token());
            UPDATE ordering.web_requests SET order_id=@oid, pickup_at=public.app_now()+interval '1 hour', pickup_before=public.app_now()+interval '5 hours' WHERE id=@id;
            """);
        createOrder.Parameters.AddWithValue("oid", fakeOrderId);
        createOrder.Parameters.AddWithValue("cid", customerId);
        createOrder.Parameters.AddWithValue("sid", sellerId);
        createOrder.Parameters.AddWithValue("kid", kiosk.Id);
        createOrder.Parameters.AddWithValue("id", customReqId);
        await createOrder.ExecuteNonQueryAsync();

        // Release slotW to RENTED_EMPTY for test
        await using var freeSlotW = source.CreateCommand("UPDATE kiosk_ops.slots SET status='RENTED_EMPTY', bouquet_id=NULL, hold_until=NULL, hold_order_id=NULL WHERE id=@slot");
        freeSlotW.Parameters.AddWithValue("slot", slotW);
        await freeSlotW.ExecuteNonQueryAsync();

        await using var fulfillWalkIn = source.CreateCommand("SELECT flow.web_fulfill(@id, @sid, @actor, @slot, 'QR-CUSTOM-W')");
        fulfillWalkIn.Parameters.AddWithValue("id", customReqId);
        fulfillWalkIn.Parameters.AddWithValue("sid", sellerId);
        fulfillWalkIn.Parameters.AddWithValue("actor", customerId);
        fulfillWalkIn.Parameters.AddWithValue("slot", slotW);
        var exFulfill = await Assert.ThrowsAsync<PostgresException>(() => fulfillWalkIn.ExecuteScalarAsync());
        Assert.Contains("PREORDER", exFulfill.MessageText);
    }

    [Fact]
    public async Task Lifecycle_ExactAnnualExpiry_ThroughJobsAndCatalog()
    {
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        var sellerId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var prodId = Guid.NewGuid();
        var bqId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var payId = Guid.NewGuid();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var handoverTs = new DateTime(2026, 10, 15, 14, 0, 0, DateTimeKind.Utc);
        var expiryTs = handoverTs.AddYears(1); // 2027-10-15 14:00:00 UTC
        var qrExp = $"QR-EXP-{Guid.NewGuid():N}"[..20];
        var kioskCode = $"K-EXP-{Guid.NewGuid():N}"[..12];
        var hwId = $"ESP-EXP-{Guid.NewGuid():N}"[..16];

        await using var setup = source.CreateCommand("""
            INSERT INTO payment.payments(id, kind, purpose, amount, gateway, idempotency_key, status, paid_at)
            VALUES(@payId, 'CHARGE', 'KIOSK_APPLICATION', 10000, 'PAYOS', 'exp-pay-' || @sid::text, 'SUCCEEDED', @handoverTs);

            INSERT INTO identity.sellers(id, shop_name, phone, address, status, package_id, package_expires_at, package_expires_at_ts)
            VALUES(@sid, 'Tiệm Hoa Hạn 1 Năm', '0901239876', 'Address', 'ACTIVE', @pkg, @expDate, @expTs);

            INSERT INTO identity.users(id, email, full_name, password_hash, role, status, seller_id)
            VALUES(@oid, @oe, 'Owner Exp', '!hash', 'SELLER', 'ACTIVE', @sid);

            INSERT INTO identity.subscriptions(id, seller_id, package_id, period_from, period_to, period_from_ts, period_to_ts, price, status, payment_id)
            VALUES(@subId, @sid, @pkg, @fromDate, @expDate, @handoverTs, @expTs, 10000, 'ACTIVE', @payId);

            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status)
            VALUES(@kid, @kcode, 'Kiosk Expiry Test', 'Address', 'HCM', @hwId, lower(@kcode), 'ONLINE');

            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, relay_channel, status, current_seller_id)
            VALUES(@slotId, @kid, 'W01', 0, 'RENTED_EMPTY', @sid);

            INSERT INTO kiosk_ops.slot_assignments(slot_id, seller_id, assigned_by, assigned_at, status)
            VALUES(@slotId, @sid, @oid, @handoverTs, 'ACTIVE');

            INSERT INTO catalog.flower_products(id, seller_id, name, description, price, tags, shelf_life_hours, status)
            VALUES(@pid, @sid, 'Hoa Thử Hạn', 'Test', 25000, ARRAY['ROMANTIC'], 720, 'ACTIVE');

            INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
            VALUES(@bqId, @pid, @sid, @qrExp, 25000, 'STOCKED', @expTs + interval '30 days', @handoverTs);

            UPDATE kiosk_ops.slots SET status='STOCKED', bouquet_id=@bqId WHERE id=@slotId;
            """);
        setup.Parameters.AddWithValue("payId", payId);
        setup.Parameters.AddWithValue("oid", ownerId);
        setup.Parameters.AddWithValue("oe", $"oexp-{ownerId:N}@example.invalid");
        setup.Parameters.AddWithValue("sid", sellerId);
        setup.Parameters.AddWithValue("pkg", trialPkg.Id);
        setup.Parameters.AddWithValue("expDate", DateOnly.FromDateTime(expiryTs));
        setup.Parameters.AddWithValue("expTs", expiryTs);
        setup.Parameters.AddWithValue("subId", subId);
        setup.Parameters.AddWithValue("fromDate", DateOnly.FromDateTime(handoverTs));
        setup.Parameters.AddWithValue("handoverTs", handoverTs);
        setup.Parameters.AddWithValue("kid", kioskId);
        setup.Parameters.AddWithValue("kcode", kioskCode);
        setup.Parameters.AddWithValue("hwId", hwId);
        setup.Parameters.AddWithValue("slotId", slotId);
        setup.Parameters.AddWithValue("pid", prodId);
        setup.Parameters.AddWithValue("bqId", bqId);
        setup.Parameters.AddWithValue("qrExp", qrExp);
        await setup.ExecuteNonQueryAsync();

        await using var conn = await source.OpenConnectionAsync();
        try
        {
            // 1. Anniversary morning: 2027-10-15 08:00:00 UTC (6 hours before exact expiry)
            var morningTs = new DateTime(2027, 10, 15, 8, 0, 0, DateTimeKind.Utc);
            await using (var tx1 = await conn.BeginTransactionAsync())
            {
                await using var rollMorning = new NpgsqlCommand("SELECT flow.roll_subscriptions(@d, @sid, @ts)", conn, tx1);
                rollMorning.Parameters.AddWithValue("d", DateOnly.FromDateTime(morningTs));
                rollMorning.Parameters.AddWithValue("sid", sellerId);
                rollMorning.Parameters.AddWithValue("ts", morningTs);
                await rollMorning.ExecuteNonQueryAsync();

                // Seller and Subscription MUST STILL BE ACTIVE!
                await using var checkSellerMorning = new NpgsqlCommand("SELECT status, flow.package_valid(@sid), flow.slot_sellable(@slot, @sid) FROM identity.sellers WHERE id=@sid", conn, tx1);
                checkSellerMorning.Parameters.AddWithValue("sid", sellerId);
                checkSellerMorning.Parameters.AddWithValue("slot", slotId);
                await using (var r = await checkSellerMorning.ExecuteReaderAsync())
                {
                    Assert.True(await r.ReadAsync());
                    Assert.Equal("ACTIVE", r.GetString(0));
                    Assert.True(r.GetBoolean(1)); // package_valid = true
                    Assert.True(r.GetBoolean(2)); // slot_sellable = true
                }

                // Bouquet MUST be visible in screen.v_kiosk_catalog and flow.ai_candidates
                await using var checkCatMorning = new NpgsqlCommand("SELECT count(*) FROM screen.v_kiosk_catalog WHERE bouquet_id=@bid", conn, tx1);
                checkCatMorning.Parameters.AddWithValue("bid", bqId);
                Assert.Equal(1L, await checkCatMorning.ExecuteScalarAsync());

                await using var checkAiMorning = new NpgsqlCommand("SELECT count(*) FROM flow.ai_candidates(@kid, 50000, 'ROMANTIC') WHERE bouquet_id=@bid", conn, tx1);
                checkAiMorning.Parameters.AddWithValue("kid", kioskId);
                checkAiMorning.Parameters.AddWithValue("bid", bqId);
                Assert.Equal(1L, await checkAiMorning.ExecuteScalarAsync());

                await tx1.CommitAsync();
            }

            // 2. Exact anniversary + 1 second: 2027-10-15 14:00:01 UTC
            var expiredTs = expiryTs.AddSeconds(1);
            await using (var tx2 = await conn.BeginTransactionAsync())
            {
                await using var rollExpired = new NpgsqlCommand("SELECT flow.roll_subscriptions(@d, @sid, @ts)", conn, tx2);
                rollExpired.Parameters.AddWithValue("d", DateOnly.FromDateTime(expiredTs));
                rollExpired.Parameters.AddWithValue("sid", sellerId);
                rollExpired.Parameters.AddWithValue("ts", expiredTs);
                await rollExpired.ExecuteNonQueryAsync();

                // Subscription status is now EXPIRED, seller is now PAST_DUE
                await using var checkSellerExpired = new NpgsqlCommand("SELECT status, flow.package_valid(@sid), flow.slot_sellable(@slot, @sid) FROM identity.sellers WHERE id=@sid", conn, tx2);
                checkSellerExpired.Parameters.AddWithValue("sid", sellerId);
                checkSellerExpired.Parameters.AddWithValue("slot", slotId);
                await using (var r = await checkSellerExpired.ExecuteReaderAsync())
                {
                    Assert.True(await r.ReadAsync());
                    Assert.Equal("PAST_DUE", r.GetString(0));
                    Assert.False(r.GetBoolean(1)); // package_valid = false
                    Assert.False(r.GetBoolean(2)); // slot_sellable = false
                }

                // Bouquet MUST NOT be visible in screen.v_kiosk_catalog or flow.ai_candidates
                await using var checkCatExpired = new NpgsqlCommand("SELECT count(*) FROM screen.v_kiosk_catalog WHERE bouquet_id=@bid", conn, tx2);
                checkCatExpired.Parameters.AddWithValue("bid", bqId);
                Assert.Equal(0L, await checkCatExpired.ExecuteScalarAsync());

                await using var checkAiExpired = new NpgsqlCommand("SELECT count(*) FROM flow.ai_candidates(@kid, 50000, 'ROMANTIC') WHERE bouquet_id=@bid", conn, tx2);
                checkAiExpired.Parameters.AddWithValue("kid", kioskId);
                checkAiExpired.Parameters.AddWithValue("bid", bqId);
                Assert.Equal(0L, await checkAiExpired.ExecuteScalarAsync());

                await tx2.CommitAsync();
            }

            // 3. Past-due grace day (+8 days): 2027-10-23 14:00:01 UTC
            var graceTs = expiryTs.AddDays(8);
            await using (var tx3 = await conn.BeginTransactionAsync())
            {
                await using var rollGrace = new NpgsqlCommand("SELECT flow.roll_subscriptions(@d, @sid, @ts)", conn, tx3);
                rollGrace.Parameters.AddWithValue("d", DateOnly.FromDateTime(graceTs));
                rollGrace.Parameters.AddWithValue("sid", sellerId);
                rollGrace.Parameters.AddWithValue("ts", graceTs);
                await rollGrace.ExecuteNonQueryAsync();

                // Slot assignments MUST be RELEASED
                await using var checkReleased = new NpgsqlCommand("SELECT status, release_reason FROM kiosk_ops.slot_assignments WHERE slot_id=@slot", conn, tx3);
                checkReleased.Parameters.AddWithValue("slot", slotId);
                await using (var r = await checkReleased.ExecuteReaderAsync())
                {
                    Assert.True(await r.ReadAsync());
                    Assert.Equal("RELEASED", r.GetString(0));
                    Assert.Equal("PAST_DUE_GRACE", r.GetString(1));
                }

                await tx3.CommitAsync();
            }
        }
        finally
        {
            await using var resetClock = new NpgsqlCommand("SELECT flow.tick(NULL)", conn);
            await resetClock.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Lifecycle_FullRefundAccounting_ZeroBalances_AndAtomicProofIsolation()
    {
        var admin1 = Guid.NewGuid();
        var admin2 = Guid.NewGuid();
        var custId = Guid.NewGuid();
        using var client1 = CreateClientForRole("ADMIN", userId: admin1);
        using var client2 = CreateClientForRole("ADMIN", userId: admin2);
        using var custClient = CreateClientForRole("CUSTOMER", userId: custId);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@a1, @e1, 'Ref Admin 1', '!hash', 'ADMIN', 'ACTIVE'),
              (@a2, @e2, 'Ref Admin 2', '!hash', 'ADMIN', 'ACTIVE'),
              (@cid, @ce, 'Ref Cust', '!hash', 'CUSTOMER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("a1", admin1);
        insertUsers.Parameters.AddWithValue("e1", $"ra1-{admin1:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("a2", admin2);
        insertUsers.Parameters.AddWithValue("e2", $"ra2-{admin2:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("cid", custId);
        insertUsers.Parameters.AddWithValue("ce", $"rcust-{custId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        // 1. Customer applies
        var appRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Tiệm Hoa Kế Toán Hoàn", "0901237777", "acc@test.vn", "777 Ledger St, Q1", 10.77, 106.69, "Vietcombank", "NGUYEN VAN ACC", "99001122"
        ));
        appRes.EnsureSuccessStatusCode();
        var app = (await appRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        await using var reserve = source.CreateCommand("SELECT flow.reserve_gateway_order(@pid)");
        reserve.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        var orderCode = (long)(await reserve.ExecuteScalarAsync())!;

        // Settle payment via reconcile mock
        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PAYOS_CLIENT_ID", "test-client");
            builder.UseSetting("PAYOS_API_KEY", "test-key");
            builder.UseSetting("PAYOS_RETURN_URL", "https://payos.vn/return");
            builder.UseSetting("PAYOS_CANCEL_URL", "https://payos.vn/cancel");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<PayOsClient>().ConfigurePrimaryHttpMessageHandler(() => new MockReconcileHandler(orderCode, 10000, "PAID"));
            });
        });

        using var customClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        customClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token("CUSTOMER", userId: custId));

        var recRes = await customClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/reconcile", new { });
        recRes.EnsureSuccessStatusCode();

        // Assert Step 1 Ledger: GATEWAY_CLEARING = +10000, PLATFORM_SUBSCRIPTION = -10000, REFUND_CLEARING = 0
        await using var checkL1 = source.CreateCommand("""
            SELECT account, sum(amount) FROM payment.ledger_entries
            WHERE (ref_type='KIOSK_APPLICATION' AND ref_id=@aid) OR (ref_type='REFUND' AND ref_id=@pid)
            GROUP BY account
            """);
        checkL1.Parameters.AddWithValue("aid", app.Id);
        checkL1.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        var balances1 = new Dictionary<string, long>();
        await using (var r = await checkL1.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) balances1[r.GetString(0)] = r.GetInt64(1);
        }
        Assert.Equal(10000L, balances1["GATEWAY_CLEARING"]);
        Assert.Equal(-10000L, balances1["PLATFORM_SUBSCRIPTION"]);
        Assert.False(balances1.ContainsKey("REFUND_CLEARING"));

        // 2. Customer voluntary cancel -> REFUND_PENDING
        var cancelRes = await customClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/cancel", new CancelKioskApplicationRequest("Khách đổi ý"));
        cancelRes.EnsureSuccessStatusCode();

        // Assert Step 2 Ledger: PLATFORM_SUBSCRIPTION = 0 (reversed!), REFUND_CLEARING = -10000 (liability)
        await using var checkL2 = source.CreateCommand("""
            SELECT account, sum(amount) FROM payment.ledger_entries
            WHERE (ref_type='KIOSK_APPLICATION' AND ref_id=@aid) OR (ref_type='REFUND' AND ref_id=@pid)
            GROUP BY account
            """);
        checkL2.Parameters.AddWithValue("aid", app.Id);
        checkL2.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        var balances2 = new Dictionary<string, long>();
        await using (var r = await checkL2.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) balances2[r.GetString(0)] = r.GetInt64(1);
        }
        Assert.Equal(10000L, balances2["GATEWAY_CLEARING"]);
        Assert.Equal(0L, balances2["PLATFORM_SUBSCRIPTION"]);
        Assert.Equal(-10000L, balances2["REFUND_CLEARING"]);

        // 3. Admin 1 approves refund
        var appApprove = await client1.PostAsJsonAsync($"/api/admin/kiosk-applications/{app.Id}/refund/approve", new { });
        appApprove.EnsureSuccessStatusCode();

        // 4. Admin 2 confirms payout with proof
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();
        var proofUrl = $"https://proof.example/{Guid.NewGuid():N}.jpg";
        var ticket = tickets.Issue("confirm_refund", PrivateEvidence.AdminBinding(admin2.ToString(), app.Id),
            new VerifiedMedia(proofUrl, "proof-file", "image/jpeg", 2048, "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2"));

        var refTxn = $"VCB-FULL-{Guid.NewGuid():N}"[..20];
        var confirmRes = await client2.PostAsJsonAsync($"/api/admin/kiosk-applications/{app.Id}/refund/confirm", new ConfirmKioskRefundRequest(refTxn, ticket.Reference));
        confirmRes.EnsureSuccessStatusCode();

        // Assert Step 3 Ledger: ALL ACCOUNTS COMPLETE LIFECYCLE SUM MUST BE EXACTLY ZERO!
        await using var checkL3 = source.CreateCommand("""
            SELECT account, sum(amount) FROM payment.ledger_entries
            WHERE (ref_type='KIOSK_APPLICATION' AND ref_id=@aid)
               OR (ref_type='REFUND' AND ref_id=@pid)
               OR (ref_type='REFUND_PAYOUT' AND ref_id IN (SELECT id FROM payment.payments WHERE parent_payment_id=@pid))
            GROUP BY account
            """);
        checkL3.Parameters.AddWithValue("aid", app.Id);
        checkL3.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        var balances3 = new Dictionary<string, long>();
        await using (var r = await checkL3.ExecuteReaderAsync())
        {
            while (await r.ReadAsync()) balances3[r.GetString(0)] = r.GetInt64(1);
        }
        Assert.Equal(0L, balances3["GATEWAY_CLEARING"]);
        Assert.Equal(0L, balances3["PLATFORM_SUBSCRIPTION"]);
        Assert.Equal(0L, balances3["REFUND_CLEARING"]);

        // Duplicate replay idempotency with same reference returns 200 OK without adding entries
        var replayRes = await client2.PostAsJsonAsync($"/api/admin/kiosk-applications/{app.Id}/refund/confirm", new ConfirmKioskRefundRequest(refTxn, ticket.Reference));
        replayRes.EnsureSuccessStatusCode();

        // Phase Isolation: Tech cannot fetch PROOF attachment
        var techId = Guid.NewGuid();
        await using var insertTech = source.CreateCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@tid, @te, 'Tech Iso', '!hash', 'TECHNICIAN', 'ACTIVE') ON CONFLICT (id) DO NOTHING");
        insertTech.Parameters.AddWithValue("tid", techId);
        insertTech.Parameters.AddWithValue("te", $"tiso-{techId:N}@example.invalid");
        await insertTech.ExecuteNonQueryAsync();

        // Assign tech to app
        await using var setTech = source.CreateCommand("UPDATE kiosk_ops.kiosk_applications SET assigned_technician_id=@tid WHERE id=@aid");
        setTech.Parameters.AddWithValue("tid", techId);
        setTech.Parameters.AddWithValue("aid", app.Id);
        await setTech.ExecuteNonQueryAsync();

        var proofAttId = await db.KioskApplications.AsNoTracking().Where(x => x.Id == app.Id).Select(x => x.Id).SingleAsync();
        await using var getAttIdCmd = source.CreateCommand("SELECT id FROM notify.attachments WHERE owner_id=@aid AND phase='PROOF' LIMIT 1");
        getAttIdCmd.Parameters.AddWithValue("aid", app.Id);
        var actualProofAttId = (Guid)(await getAttIdCmd.ExecuteScalarAsync())!;

        using var techClient = CreateClientForRole("TECHNICIAN", userId: techId);
        var techTriesProof = await techClient.GetAsync($"/api/staff/kiosk-installations/{app.Id}/evidence/{actualProofAttId}");
        Assert.Equal(HttpStatusCode.NotFound, techTriesProof.StatusCode);
    }

    [Fact]
    public async Task Lifecycle_ReconcileCapOutboxAndAdminPolicy()
    {
        var custId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var foreignCustId = Guid.NewGuid();
        var opsMgrId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@cid, @ce, 'Reconcile Owner', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@aid, @ae, 'Reconcile Admin', '!hash', 'ADMIN', 'ACTIVE'),
              (@fid, @fe, 'Foreign Customer', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@mid, @me, 'Reconcile Ops', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("cid", custId);
        insertUsers.Parameters.AddWithValue("ce", $"reconcile-owner-{custId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("aid", adminId);
        insertUsers.Parameters.AddWithValue("ae", $"reconcile-admin-{adminId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("fid", foreignCustId);
        insertUsers.Parameters.AddWithValue("fe", $"reconcile-foreign-{foreignCustId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("mid", opsMgrId);
        insertUsers.Parameters.AddWithValue("me", $"reconcile-ops-{opsMgrId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        using var ownerClient = CreateClientForRole("CUSTOMER", userId: custId);
        var appRes = await ownerClient.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Tiệm Hoa CAP Outbox", "0901235555", "cap@test.vn", "888 CAP Avenue, Q1", 10.78, 106.70, "VCB", "CHỦ TIỆM", "55667788"
        ));
        appRes.EnsureSuccessStatusCode();
        var app = (await appRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        await using var reserve = source.CreateCommand("SELECT flow.reserve_gateway_order(@pid)");
        reserve.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        var orderCode = (long)(await reserve.ExecuteScalarAsync())!;

        using var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("PAYOS_CLIENT_ID", "test-client");
            builder.UseSetting("PAYOS_API_KEY", "test-key");
            builder.UseSetting("PAYOS_RETURN_URL", "https://payos.vn/return");
            builder.UseSetting("PAYOS_CANCEL_URL", "https://payos.vn/cancel");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient<PayOsClient>().ConfigurePrimaryHttpMessageHandler(() => new MockReconcileHandler(orderCode, 10000, "PAID"));
            });
        });

        // 1. Kiosk JWT (with kiosk_id claim) must be DENIED (403 Forbidden)
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
        var kioskUser = new User { Id = custId, Role = "CUSTOMER", FullName = "Kiosk Device User" };
        var kioskToken = tokenService.Issue(kioskUser, kioskId: Guid.NewGuid().ToString());
        using var kioskClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        kioskClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", kioskToken);

        var kioskDenial = await kioskClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/reconcile", new { });
        Assert.Equal(HttpStatusCode.Forbidden, kioskDenial.StatusCode);

        // 2. Operations Manager must be DENIED (403 Forbidden)
        using var opsClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        opsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token("OPERATIONS_MANAGER", userId: opsMgrId));
        var opsDenial = await opsClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/reconcile", new { });
        Assert.Equal(HttpStatusCode.Forbidden, opsDenial.StatusCode);

        // 3. Foreign customer must get 404 Not Found
        using var foreignClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        foreignClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token("CUSTOMER", userId: foreignCustId));
        var foreignDenial = await foreignClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/reconcile", new { });
        Assert.Equal(HttpStatusCode.NotFound, foreignDenial.StatusCode);

        // 4. Admin CAN call reconcile -> 200 OK!
        using var adminClient = customFactory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Token("ADMIN", userId: adminId));
        var adminRes = await adminClient.PostAsJsonAsync($"/api/member/kiosk-applications/{app.Id}/reconcile", new { });
        adminRes.EnsureSuccessStatusCode();
        var reconciledApp = await adminRes.Content.ReadFromJsonAsync<KioskApplicationResponse>();
        Assert.NotNull(reconciledApp);
        Assert.Equal("PAID_REVIEW", reconciledApp.Status);

        // 5. Verify CAP Outbox message was published
        await using var checkCap = source.CreateCommand("""
            SELECT count(*) FROM cap.published
            WHERE "Name" = 'florabot.payment.succeeded.v1'
              AND "Content"::jsonb->'Value'->>'payment_id' = @pid::text
            """);
        checkCap.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        Assert.True((long)(await checkCap.ExecuteScalarAsync())! >= 1L);

        // Verify NO flower order or subscription paid event was published for this KIOSK_APPLICATION
        await using var checkOtherEvents = source.CreateCommand("""
            SELECT count(*) FROM cap.published
            WHERE "Name" IN ('florabot.order.paid.v1', 'florabot.subscription.paid.v1')
              AND "Content"::jsonb->'Value'->>'payment_id' = @pid::text
            """);
        checkOtherEvents.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        Assert.Equal(0L, await checkOtherEvents.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Lifecycle_ScheduleOverlapRangeConcurrency()
    {
        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();
        var cust1 = Guid.NewGuid();
        var cust2 = Guid.NewGuid();
        var cust3 = Guid.NewGuid();
        var cust4 = Guid.NewGuid();
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var scope = _factory.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@mid, @me, 'Range Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE'),
              (@tid, @te, 'Range Tech', '!hash', 'TECHNICIAN', 'ACTIVE'),
              (@c1, @ce1, 'Range Cust 1', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@c2, @ce2, 'Range Cust 2', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@c3, @ce3, 'Range Cust 3', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@c4, @ce4, 'Range Cust 4', '!hash', 'CUSTOMER', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("me", $"rmgr-{managerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("tid", techId);
        insertUsers.Parameters.AddWithValue("te", $"rtech-{techId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("c1", cust1);
        insertUsers.Parameters.AddWithValue("ce1", $"rc1-{cust1:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("c2", cust2);
        insertUsers.Parameters.AddWithValue("ce2", $"rc2-{cust2:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("c3", cust3);
        insertUsers.Parameters.AddWithValue("ce3", $"rc3-{cust3:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("c4", cust4);
        insertUsers.Parameters.AddWithValue("ce4", $"rc4-{cust4:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        async Task<(EnrollKioskResponse Kiosk, KioskApplicationResponse App)> CreateAndConfirm(Guid cid, string shop)
        {
            using var c = CreateClientForRole("CUSTOMER", userId: cid);
            var kRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
                $"K-RNG-{Guid.NewGuid():N}"[..12], "Kiosk Range Test", $"ESP-RNG-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
            ));
            kRes.EnsureSuccessStatusCode();
            var k = (await kRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

            var aRes = await c.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
                trialPkg.Id, shop, "0901236666", "rng@test.vn", "123 Range St", 10.7, 106.7, "VCB", "CHỦ", "667788"
            ));
            aRes.EnsureSuccessStatusCode();
            var a = (await aRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

            await using var pCmd = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
            pCmd.Parameters.AddWithValue("pid", a.PaymentId!.Value);
            pCmd.Parameters.AddWithValue("txn", $"PAYOS-RNG-{Guid.NewGuid():N}");
            await pCmd.ExecuteNonQueryAsync();

            var conf = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{a.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận"));
            conf.EnsureSuccessStatusCode();
            return (k, a);
        }

        var (kiosk1, app1) = await CreateAndConfirm(cust1, "Shop Range 1");
        var (kiosk2, app2) = await CreateAndConfirm(cust2, "Shop Range 2");
        var (kiosk3, app3) = await CreateAndConfirm(cust3, "Shop Range 3");
        var (kiosk4, app4) = await CreateAndConfirm(cust4, "Shop Range 4");

        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5));

        // 1. Invalid time window formats -> 400 Bad Request
        var badFmt1 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app1.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "9:00 - 12:00", kiosk1.Id
        ));
        Assert.Equal(HttpStatusCode.BadRequest, badFmt1.StatusCode);

        var badFmt2 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app1.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "14:00 - 10:00", kiosk1.Id
        ));
        Assert.Equal(HttpStatusCode.BadRequest, badFmt2.StatusCode);

        var badFmt3 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app1.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "invalid time window", kiosk1.Id
        ));
        Assert.Equal(HttpStatusCode.BadRequest, badFmt3.StatusCode);

        // 2. Schedule App 1 for 09:00 - 12:00 -> 200 OK
        var s1 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app1.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "09:00 - 12:00", kiosk1.Id
        ));
        s1.EnsureSuccessStatusCode();

        // 3. Overlap checks with same technician on same date:
        // App 2 tries 10:00 - 13:00 (overlaps with 09:00 - 12:00) -> 409 Conflict
        var overlap1 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app2.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "10:00 - 13:00", kiosk2.Id
        ));
        Assert.Equal(HttpStatusCode.Conflict, overlap1.StatusCode);

        // App 2 tries 08:00 - 10:00 (overlaps with 09:00 - 12:00) -> 409 Conflict
        var overlap2 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app2.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "08:00 - 10:00", kiosk2.Id
        ));
        Assert.Equal(HttpStatusCode.Conflict, overlap2.StatusCode);

        // 4. Non-overlapping window: 12:00 - 15:00 -> 200 OK
        var s2 = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app2.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "12:00 - 15:00", kiosk2.Id
        ));
        s2.EnsureSuccessStatusCode();

        // 5. Overlap concurrency race on same technician:
        // App 3 and App 4 concurrently try to schedule the same technician for 15:00 - 18:00
        var task3 = mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app3.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "15:00 - 18:00", kiosk3.Id
        ));
        var task4 = mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app4.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date, "15:00 - 18:00", kiosk4.Id
        ));
        var results = await Task.WhenAll(task3, task4);

        var statuses = results.Select(r => r.StatusCode).ToList();
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        // 6. Kiosk with FAULT slot cannot be scheduled -> 409 Conflict
        var faultyKioskRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-FLT-{Guid.NewGuid():N}"[..12], "Kiosk Faulty", $"ESP-FLT-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        faultyKioskRes.EnsureSuccessStatusCode();
        var faultyKiosk = (await faultyKioskRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        await using var setFault = source.CreateCommand("UPDATE kiosk_ops.slots SET status='FAULT' WHERE kiosk_id=@kid AND zone='WALK_IN'");
        setFault.Parameters.AddWithValue("kid", faultyKiosk.Id);
        await setFault.ExecuteNonQueryAsync();

        // Pick whichever app didn't win the race
        var loserApp = results[0].StatusCode == HttpStatusCode.Conflict ? app3 : app4;
        var scheduleFaulty = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{loserApp.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, date.AddDays(1), "09:00 - 12:00", faultyKiosk.Id
        ));
        Assert.Equal(HttpStatusCode.Conflict, scheduleFaulty.StatusCode);
    }

    [Fact]
    public async Task EvidenceAttachment_StaleOrConcurrentTransition_RejectsAttachAndLeavesNoOrphanEvidenceOrAudit()
    {
        var customerId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();
        var foreignTechId = Guid.NewGuid();

        using var custClient = CreateClientForRole("CUSTOMER", userId: customerId);
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var techClient = CreateClientForRole("TECHNICIAN", userId: techId);
        using var foreignTechClient = CreateClientForRole("TECHNICIAN", userId: foreignTechId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var tickets = scope.ServiceProvider.GetRequiredService<PrivateEvidence>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@cid, @cemail, 'Stale Cust', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@mid, @memail, 'Stale Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE'),
              (@tid, @temail, 'Stale Tech', '!hash', 'TECHNICIAN', 'ACTIVE'),
              (@fid, @femail, 'Foreign Tech', '!hash', 'TECHNICIAN', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("cid", customerId);
        insertUsers.Parameters.AddWithValue("cemail", $"stale-cust-{customerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("memail", $"stale-mgr-{managerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("tid", techId);
        insertUsers.Parameters.AddWithValue("temail", $"stale-tech-{techId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("fid", foreignTechId);
        insertUsers.Parameters.AddWithValue("femail", $"stale-foreign-{foreignTechId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        var enrollRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-STL-{Guid.NewGuid():N}"[..12], "Kiosk Stale Test", $"ESP-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        enrollRes.EnsureSuccessStatusCode();
        var kiosk = (await enrollRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        var appRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Stale Shop", "0901234777", "stale@example.invalid", "123 Stale Rd", 10.7, 106.6, "VCB", "CHỦ", "123456"
        ));
        appRes.EnsureSuccessStatusCode();
        var app = (await appRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Pay -> PAID_REVIEW
        await using var pay = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        pay.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        pay.Parameters.AddWithValue("txn", $"PAYOS-STL-{Guid.NewGuid():N}");
        await pay.ExecuteNonQueryAsync();

        // Confirm
        (await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận thông tin khách hàng"))).EnsureSuccessStatusCode();

        // Schedule
        (await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "09:00 - 12:00", kiosk.Id
        ))).EnsureSuccessStatusCode();

        // Tech starts -> INSTALLING
        (await techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/start", new { })).EnsureSuccessStatusCode();

        // Issue valid media ticket for assigned tech
        var uniqueUrl = $"https://res.cloudinary.com/demo/image/upload/stale-{Guid.NewGuid():N}.jpg";
        var ticket = tickets.Issue("kiosk_installation", $"tech-kiosk:{techId}:{app.Id}",
            new VerifiedMedia(uniqueUrl, "stale-evidence", "image/jpeg", 2048, "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592"));

        // Keep the manager's transition uncommitted: the API precheck still reads
        // INSTALLING, but attachment must wait on the application row lock and
        // revalidate REFUND_PENDING after that transition commits.
        await using var failureConnection = await source.OpenConnectionAsync();
        await using var failureTransaction = await failureConnection.BeginTransactionAsync();
        await using var failureCommand = new NpgsqlCommand("SELECT flow.kiosk_application_platform_failure(@id, @actor, @reason)", failureConnection, failureTransaction);
        failureCommand.Parameters.AddWithValue("id", app.Id);
        failureCommand.Parameters.AddWithValue("actor", managerId);
        failureCommand.Parameters.AddWithValue("reason", "Mặt bằng không đủ điều kiện lắp đặt tủ");
        await failureCommand.ExecuteScalarAsync();

        var attachTask = techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/evidence",
            new AttachKioskEvidenceRequest(Guid.NewGuid(), ticket.Reference));
        var observedRowLock = false;
        for (var attempt = 0; attempt < 60 && !attachTask.IsCompleted; attempt++)
        {
            await using var waitingQuery = source.CreateCommand("""
                SELECT EXISTS (
                  SELECT 1 FROM pg_stat_activity
                  WHERE datname = current_database() AND wait_event_type = 'Lock'
                    AND query LIKE '%flow.attach_kiosk_evidence%'
                )
                """);
            observedRowLock = (bool)(await waitingQuery.ExecuteScalarAsync())!;
            if (observedRowLock) break;
            await Task.Delay(50);
        }
        await failureTransaction.CommitAsync();
        var attachAttempt = await attachTask;
        Assert.True(observedRowLock, "Attachment must serialize with the in-flight manager transition, rather than trust its stale API precheck.");
        Assert.Equal(HttpStatusCode.Conflict, attachAttempt.StatusCode);

        // SQL verification: NO orphan attachment row exists
        await using var countAtt = source.CreateCommand("SELECT count(*) FROM notify.attachments WHERE owner_service='kiosk_ops' AND owner_type='kiosk_application' AND owner_id=@appId AND phase='EVIDENCE'");
        countAtt.Parameters.AddWithValue("appId", app.Id);
        var attCount = (long)(await countAtt.ExecuteScalarAsync())!;
        Assert.Equal(0, attCount);

        // SQL verification: NO audit log for attachment exists
        await using var countAudit = source.CreateCommand("SELECT count(*) FROM notify.audit_logs WHERE action='KIOSK_APPLICATION_EVIDENCE_ATTACHED' AND entity_id=@appId");
        countAudit.Parameters.AddWithValue("appId", app.Id);
        var auditCount = (long)(await countAudit.ExecuteScalarAsync())!;
        Assert.Equal(0, auditCount);

        // Direct SQL flow verification: calling flow.attach_kiosk_evidence on non-INSTALLING app raises P0001
        var sqlAttach = source.CreateCommand("SELECT flow.attach_kiosk_evidence(@id, @appId, @url, 'image/jpeg', 1024, 'dummy', @actor)");
        sqlAttach.Parameters.AddWithValue("id", Guid.NewGuid());
        sqlAttach.Parameters.AddWithValue("appId", app.Id);
        sqlAttach.Parameters.AddWithValue("url", $"https://res.cloudinary.com/demo/image/upload/sql-{Guid.NewGuid():N}.jpg");
        sqlAttach.Parameters.AddWithValue("actor", techId);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => sqlAttach.ExecuteScalarAsync());
        Assert.Equal("P0001", ex.SqlState);
        Assert.Contains("INSTALLING", ex.MessageText);

        // Direct SQL flow verification: calling flow.attach_kiosk_evidence with wrong technician raises 42501
        var wrongTechSql = source.CreateCommand("SELECT flow.attach_kiosk_evidence(@id, @appId, @url, 'image/jpeg', 1024, 'dummy', @actor)");
        wrongTechSql.Parameters.AddWithValue("id", Guid.NewGuid());
        wrongTechSql.Parameters.AddWithValue("appId", app.Id);
        wrongTechSql.Parameters.AddWithValue("url", $"https://res.cloudinary.com/demo/image/upload/wrong-{Guid.NewGuid():N}.jpg");
        wrongTechSql.Parameters.AddWithValue("actor", foreignTechId);
        var exTech = await Assert.ThrowsAsync<PostgresException>(() => wrongTechSql.ExecuteScalarAsync());
        Assert.Equal("42501", exTech.SqlState);
    }

    [Fact]
    public async Task Handover_WhenCustomerRoleChangedToStaffBeforeHandover_RejectsAndNeverPromotesOrAllocates()
    {
        var customerId = Guid.NewGuid();
        var managerId = Guid.NewGuid();
        var techId = Guid.NewGuid();

        using var custClient = CreateClientForRole("CUSTOMER", userId: customerId);
        using var mgrClient = CreateClientForRole("OPERATIONS_MANAGER", userId: managerId);
        using var techClient = CreateClientForRole("TECHNICIAN", userId: techId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FloraDbContext>();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var insertUsers = source.CreateCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES
              (@cid, @cemail, 'Priv Change Cust', '!hash', 'CUSTOMER', 'ACTIVE'),
              (@mid, @memail, 'Priv Change Mgr', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE'),
              (@tid, @temail, 'Priv Change Tech', '!hash', 'TECHNICIAN', 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
            """);
        insertUsers.Parameters.AddWithValue("cid", customerId);
        insertUsers.Parameters.AddWithValue("cemail", $"priv-cust-{customerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("mid", managerId);
        insertUsers.Parameters.AddWithValue("memail", $"priv-mgr-{managerId:N}@example.invalid");
        insertUsers.Parameters.AddWithValue("tid", techId);
        insertUsers.Parameters.AddWithValue("temail", $"priv-tech-{techId:N}@example.invalid");
        await insertUsers.ExecuteNonQueryAsync();

        var trialPkg = await db.SubscriptionPackages.FirstAsync(x => x.Name == "Trial" && x.BillingCycle == "ANNUAL");

        // Verify flow.create_kiosk_application directly rejects non-CUSTOMER role with 42501
        await using var staffApplyCmd = source.CreateCommand("SELECT flow.create_kiosk_application(@staffId, @pkg, 'Shop', '0901112222', 'staff@test.vn', 'Addr', 10.7, 106.6, 'VCB', 'HOLDER', 'enc')");
        staffApplyCmd.Parameters.AddWithValue("staffId", managerId);
        staffApplyCmd.Parameters.AddWithValue("pkg", trialPkg.Id);
        var staffEx = await Assert.ThrowsAsync<PostgresException>(() => staffApplyCmd.ExecuteScalarAsync());
        Assert.Equal("42501", staffEx.SqlState);

        var enrollRes = await mgrClient.PostAsJsonAsync("/api/operations/kiosks/enroll", new EnrollKioskRequest(
            $"K-PRV-{Guid.NewGuid():N}"[..12], "Kiosk Priv Test", $"ESP-{Guid.NewGuid():N}"[..16], null, "HCM", 1, 1
        ));
        enrollRes.EnsureSuccessStatusCode();
        var kiosk = (await enrollRes.Content.ReadFromJsonAsync<EnrollKioskResponse>())!;

        var appRes = await custClient.PostAsJsonAsync("/api/member/kiosk-applications", new CreateKioskApplicationRequest(
            trialPkg.Id, "Shop Priv Esc", "0901234999", "priv@test.vn", "789 Priv Ave", 10.75, 106.65, "TCB", "CHỦ", "778899"
        ));
        appRes.EnsureSuccessStatusCode();
        var app = (await appRes.Content.ReadFromJsonAsync<KioskApplicationResponse>())!;

        // Settle payment -> PAID_REVIEW
        await using var pay = source.CreateCommand("SELECT flow.kiosk_application_paid(@pid, @txn)");
        pay.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        pay.Parameters.AddWithValue("txn", $"PAYOS-PRV-{Guid.NewGuid():N}");
        await pay.ExecuteNonQueryAsync();

        // Manager confirms -> CONFIRMED
        (await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/confirm", new ConfirmKioskApplicationRequest("Xác nhận thông tin khách hàng"))).EnsureSuccessStatusCode();

        // Manager schedules -> SCHEDULED
        (await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/schedule", new ScheduleKioskApplicationRequest(
            techId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), "09:00 - 12:00", kiosk.Id
        ))).EnsureSuccessStatusCode();

        // Tech starts visit -> INSTALLING
        (await techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/start", new { })).EnsureSuccessStatusCode();

        // Tech attaches evidence
        await using var attach = source.CreateCommand("SELECT flow.attach_kiosk_evidence(@id, @appId, 'https://res.cloudinary.com/demo/image/upload/sample.jpg', 'image/jpeg', 1024, 'dummy', @actor)");
        attach.Parameters.AddWithValue("id", Guid.NewGuid());
        attach.Parameters.AddWithValue("appId", app.Id);
        attach.Parameters.AddWithValue("actor", techId);
        await attach.ExecuteNonQueryAsync();

        // Tech submits report -> AWAITING_HANDOVER
        (await techClient.PostAsJsonAsync($"/api/staff/kiosk-installations/{app.Id}/submit", new SubmitInstallationReportRequest("Report done"))).EnsureSuccessStatusCode();

        // Privilege change: Customer identity is changed to a staff role (e.g. OPERATIONS_MANAGER) before handover
        await using var promoteToStaff = source.CreateCommand("UPDATE identity.users SET role='OPERATIONS_MANAGER' WHERE id=@cid");
        promoteToStaff.Parameters.AddWithValue("cid", customerId);
        await promoteToStaff.ExecuteNonQueryAsync();

        // Manager calls handover -> MUST FAIL with 409 Conflict
        var handoverRes = await mgrClient.PostAsJsonAsync($"/api/operations/kiosk-applications/{app.Id}/handover", new { });
        Assert.Equal(HttpStatusCode.Conflict, handoverRes.StatusCode);

        // SQL verification: user's role is STILL OPERATIONS_MANAGER (not overwritten to SELLER)
        await using var checkUser = source.CreateCommand("SELECT role, seller_id FROM identity.users WHERE id=@cid");
        checkUser.Parameters.AddWithValue("cid", customerId);
        await using (var reader = await checkUser.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("OPERATIONS_MANAGER", reader.GetString(0));
            Assert.True(reader.IsDBNull(1), "seller_id must remain null");
        }

        // SQL verification: NO seller row created
        await using var checkSeller = source.CreateCommand("SELECT count(*) FROM identity.sellers WHERE phone='0901234999'");
        var sellerCount = (long)(await checkSeller.ExecuteScalarAsync())!;
        Assert.Equal(0, sellerCount);

        // SQL verification: NO subscription created
        await using var checkSub = source.CreateCommand("SELECT count(*) FROM identity.subscriptions WHERE payment_id=@pid");
        checkSub.Parameters.AddWithValue("pid", app.PaymentId!.Value);
        var subCount = (long)(await checkSub.ExecuteScalarAsync())!;
        Assert.Equal(0, subCount);

        // SQL verification: NO slot assignments created
        await using var checkSlotsAssigned = source.CreateCommand("SELECT count(*) FROM kiosk_ops.slot_assignments WHERE slot_id IN (SELECT id FROM kiosk_ops.slots WHERE kiosk_id=@kid)");
        checkSlotsAssigned.Parameters.AddWithValue("kid", kiosk.Id);
        var slotAssignCount = (long)(await checkSlotsAssigned.ExecuteScalarAsync())!;
        Assert.Equal(0, slotAssignCount);

        // SQL verification: slots remain FREE with null current_seller_id
        await using var checkSlots = source.CreateCommand("SELECT count(*) FROM kiosk_ops.slots WHERE kiosk_id=@kid AND status='FREE' AND current_seller_id IS NULL");
        checkSlots.Parameters.AddWithValue("kid", kiosk.Id);
        var freeSlotCount = (long)(await checkSlots.ExecuteScalarAsync())!;
        Assert.Equal(2, freeSlotCount);

        // SQL verification: application remains in AWAITING_HANDOVER
        await using var checkApp = source.CreateCommand("SELECT status, seller_id FROM kiosk_ops.kiosk_applications WHERE id=@appId");
        checkApp.Parameters.AddWithValue("appId", app.Id);
        await using (var reader = await checkApp.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("AWAITING_HANDOVER", reader.GetString(0));
            Assert.True(reader.IsDBNull(1), "application seller_id must remain null");
        }
    }

    private sealed class MockReconcileHandler(long expectedOrderCode, long expectedAmount, string status = "PAID") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var data = new
            {
                orderCode = expectedOrderCode,
                amount = expectedAmount,
                status = status,
                reference = $"PAYOS-REC-{Guid.NewGuid():N}"
            };
            var dataJson = JsonSerializer.SerializeToElement(data);
            var signature = PayOsChecksum.Sign(dataJson, "test-only-checksum-key");

            var envelope = JsonSerializer.Serialize(new
            {
                code = "00",
                desc = "success",
                data = data,
                signature = signature
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class MockPayOsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = JsonSerializer.Serialize(new
            {
                code = "00",
                desc = "success",
                data = new
                {
                    orderCode = 12345,
                    amount = 10000,
                    status = "PENDING",
                    checkoutUrl = "https://pay.payos.vn/web/0123456789abcdef0123456789abcdef"
                },
                signature = PayOsChecksum.Sign(JsonSerializer.SerializeToElement(new
                {
                    amount = 10000,
                    checkoutUrl = "https://pay.payos.vn/web/0123456789abcdef0123456789abcdef",
                    orderCode = 12345,
                    status = "PENDING"
                }), "test-checksum-key")
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
