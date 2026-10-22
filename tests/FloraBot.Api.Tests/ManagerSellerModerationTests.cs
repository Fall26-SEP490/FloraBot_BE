using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Identity;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ManagerSellerModerationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly Guid BasicPackageId = Guid.Parse("20000000-0000-0000-0000-00000000000a");

    [Fact]
    public async Task OperationsManager_CanListAndDetailSellers_WithoutSecretFields()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var uniqueShop = "Shop Moderation Test " + Guid.NewGuid().ToString("N")[..8];
        var uniquePhone = "09" + Random.Shared.Next(10000000, 99999999);

        await using var conn = await data.OpenConnectionAsync();
        await using (var insertMgr = new NpgsqlCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Ops Mgr List', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE')", conn))
        {
            insertMgr.Parameters.AddWithValue("id", managerId);
            insertMgr.Parameters.AddWithValue("email", $"mgr_{managerId:N}@florabot.test");
            await insertMgr.ExecuteNonQueryAsync();
        }

        await using (var insertSeller = new NpgsqlCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, address, status, package_id, package_expires_at, package_expires_at_ts, contact_email, brand_tone, bank_name, bank_account_no_enc, bank_holder)
            VALUES(@id, @shop, @phone, '123 Pham Van Dong, Thu Duc', 'ACTIVE', @pkg, CURRENT_DATE + 30, public.app_now() + interval '30 days', 'contact@shop.test', 'Thân thiện', 'Vietcombank', 'enc:secret-bank-no', 'CHU SHOP')
            """, conn))
        {
            insertSeller.Parameters.AddWithValue("id", sellerId);
            insertSeller.Parameters.AddWithValue("shop", uniqueShop);
            insertSeller.Parameters.AddWithValue("phone", uniquePhone);
            insertSeller.Parameters.AddWithValue("pkg", BasicPackageId);
            await insertSeller.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            // List with search and status filter
            var listResp = await client.GetAsync($"/api/operations/sellers?status=ACTIVE&search={Uri.EscapeDataString(uniqueShop)}&page=1&pageSize=10");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            Assert.True(listResp.Headers.CacheControl?.NoStore);

            var listRawJson = await listResp.Content.ReadAsStringAsync();
            AssertNoSecretFields(listRawJson);

            var page = await listResp.Content.ReadFromJsonAsync<OperationsSellerPage>();
            Assert.NotNull(page);
            var item = Assert.Single(page.Items, s => s.Id == sellerId);
            Assert.Equal(uniqueShop, item.ShopName);
            Assert.Equal("ACTIVE", item.Status);
            Assert.Equal(BasicPackageId, item.PackageId);
            Assert.NotNull(item.PackageName);

            // Detail
            var detailResp = await client.GetAsync($"/api/operations/sellers/{sellerId}");
            Assert.Equal(HttpStatusCode.OK, detailResp.StatusCode);
            Assert.True(detailResp.Headers.CacheControl?.NoStore);

            var detailRawJson = await detailResp.Content.ReadAsStringAsync();
            AssertNoSecretFields(detailRawJson);

            var detail = await detailResp.Content.ReadFromJsonAsync<OperationsSellerDetail>();
            Assert.NotNull(detail);
            Assert.Equal(sellerId, detail.Id);
            Assert.Equal(uniqueShop, detail.ShopName);
            Assert.Equal(uniquePhone, detail.Phone);
            Assert.Equal("123 Pham Van Dong, Thu Duc", detail.Address);
            Assert.Equal("ACTIVE", detail.Status);
            Assert.Equal("contact@shop.test", detail.ContactEmail);
            Assert.Equal("Thân thiện", detail.BrandTone);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM identity.sellers WHERE id = @sid; DELETE FROM identity.users WHERE id = @uid;";
            cleanup.Parameters.AddWithValue("sid", sellerId);
            cleanup.Parameters.AddWithValue("uid", managerId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task OperationsManager_CanSuspendAndReactivate_WithMandatoryReasonAndAuditHistory()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var uniqueShop = "Shop Lifecycle " + Guid.NewGuid().ToString("N")[..8];
        var uniquePhone = "09" + Random.Shared.Next(10000000, 99999999);

        await using var conn = await data.OpenConnectionAsync();
        await using (var insertMgr = new NpgsqlCommand("INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@id, @email, 'Ops Mgr Lifecycle', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE')", conn))
        {
            insertMgr.Parameters.AddWithValue("id", managerId);
            insertMgr.Parameters.AddWithValue("email", $"mgr_{managerId:N}@florabot.test");
            await insertMgr.ExecuteNonQueryAsync();
        }

        await using (var insertSeller = new NpgsqlCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at, package_expires_at_ts)
            VALUES(@id, @shop, @phone, 'ACTIVE', @pkg, CURRENT_DATE + 30, public.app_now() + interval '30 days')
            """, conn))
        {
            insertSeller.Parameters.AddWithValue("id", sellerId);
            insertSeller.Parameters.AddWithValue("shop", uniqueShop);
            insertSeller.Parameters.AddWithValue("phone", uniquePhone);
            insertSeller.Parameters.AddWithValue("pkg", BasicPackageId);
            await insertSeller.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            // 1. Suspend ACTIVE seller
            var suspendReason = "Tạm ngưng do vi phạm quy chuẩn đóng gói hoa sáp và hoa lụa trưng bày tại kiosk";
            var suspendResp = await client.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("SUSPENDED", suspendReason));
            Assert.Equal(HttpStatusCode.OK, suspendResp.StatusCode);
            var suspendedDetail = await suspendResp.Content.ReadFromJsonAsync<OperationsSellerDetail>();
            Assert.NotNull(suspendedDetail);
            Assert.Equal("SUSPENDED", suspendedDetail.Status);

            // Verify status in DB
            await using (var checkCmd = new NpgsqlCommand("SELECT status FROM identity.sellers WHERE id = @id", conn))
            {
                checkCmd.Parameters.AddWithValue("id", sellerId);
                Assert.Equal("SUSPENDED", await checkCmd.ExecuteScalarAsync());
            }

            // Verify status-history contains suspension
            var historyResp1 = await client.GetAsync($"/api/operations/sellers/{sellerId}/status-history?page=1&pageSize=10");
            Assert.Equal(HttpStatusCode.OK, historyResp1.StatusCode);
            var history1 = await historyResp1.Content.ReadFromJsonAsync<SellerStatusHistoryResponse>();
            Assert.NotNull(history1);
            Assert.NotEmpty(history1.Items);
            var suspendLog = history1.Items[0];
            Assert.Equal("SELLER_STATUS_SUSPENDED", suspendLog.Action);
            Assert.Equal("ACTIVE", suspendLog.FromStatus);
            Assert.Equal("SUSPENDED", suspendLog.ToStatus);
            Assert.Equal(suspendReason, suspendLog.Reason);
            Assert.Equal(managerId, suspendLog.ActorId);

            // 2. Reactivate SUSPENDED seller
            var reactivateReason = "Đã kiểm tra cơ sở, xác nhận toàn bộ hoa mới đạt chuẩn chất lượng";
            var reactivateResp = await client.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("ACTIVE", reactivateReason));
            Assert.Equal(HttpStatusCode.OK, reactivateResp.StatusCode);
            var reactivatedDetail = await reactivateResp.Content.ReadFromJsonAsync<OperationsSellerDetail>();
            Assert.NotNull(reactivatedDetail);
            Assert.Equal("ACTIVE", reactivatedDetail.Status);

            // Verify status in DB
            await using (var checkCmd = new NpgsqlCommand("SELECT status FROM identity.sellers WHERE id = @id", conn))
            {
                checkCmd.Parameters.AddWithValue("id", sellerId);
                Assert.Equal("ACTIVE", await checkCmd.ExecuteScalarAsync());
            }

            // Verify status-history contains reactivation
            var historyResp2 = await client.GetAsync($"/api/operations/sellers/{sellerId}/status-history?page=1&pageSize=10");
            Assert.Equal(HttpStatusCode.OK, historyResp2.StatusCode);
            var history2 = await historyResp2.Content.ReadFromJsonAsync<SellerStatusHistoryResponse>();
            Assert.NotNull(history2);
            Assert.True(history2.Items.Count >= 2);
            var reactivateLog = history2.Items[0];
            Assert.Equal("SELLER_STATUS_ACTIVE", reactivateLog.Action);
            Assert.Equal("SUSPENDED", reactivateLog.FromStatus);
            Assert.Equal("ACTIVE", reactivateLog.ToStatus);
            Assert.Equal(reactivateReason, reactivateLog.Reason);
            Assert.Equal(managerId, reactivateLog.ActorId);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM identity.sellers WHERE id = @sid; DELETE FROM identity.users WHERE id = @uid;";
            cleanup.Parameters.AddWithValue("sid", sellerId);
            cleanup.Parameters.AddWithValue("uid", managerId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task AllWrongRolesAndAnonymous_AreDenied()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var sellerId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var technicianId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var sellerUserId = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@sid, 'Auth Shop', '0901234567', 'ACTIVE', @pkg, CURRENT_DATE + 30);
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status, seller_id) VALUES
            (@cid, 'cust@florabot.test', 'Customer', '!hash', 'CUSTOMER', 'ACTIVE', NULL),
            (@tid, 'tech@florabot.test', 'Technician', '!hash', 'TECHNICIAN', 'ACTIVE', NULL),
            (@suid, 'seller@florabot.test', 'Seller User', '!hash', 'SELLER', 'ACTIVE', @sid),
            (@sfid, 'staff@florabot.test', 'Seller Staff', '!hash', 'SELLER_STAFF', 'ACTIVE', @sid);
            """, conn))
        {
            insert.Parameters.AddWithValue("sid", sellerId);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            insert.Parameters.AddWithValue("cid", customerId);
            insert.Parameters.AddWithValue("tid", technicianId);
            insert.Parameters.AddWithValue("suid", sellerUserId);
            insert.Parameters.AddWithValue("sfid", staffId);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            var endpoints = new (string Method, string Path)[]
            {
                ("GET", "/api/operations/sellers"),
                ("GET", $"/api/operations/sellers/{sellerId}"),
                ("PATCH", $"/api/operations/sellers/{sellerId}/status"),
                ("GET", $"/api/operations/sellers/{sellerId}/status-history")
            };

            // Anonymous: 401
            using var anonClient = factory.CreateClient();
            foreach (var (method, path) in endpoints)
            {
                var req = new HttpRequestMessage(new HttpMethod(method), path);
                if (method == "PATCH") req.Content = JsonContent.Create(new UpdateSellerStatusRequest("SUSPENDED", "Tạm ngưng kiểm tra"));
                var resp = await anonClient.SendAsync(req);
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }

            // Wrong roles: 403 (CUSTOMER, SELLER, TECHNICIAN, SELLER_STAFF, ADMIN)
            var wrongRoleTokens = new (string Role, string Token)[]
            {
                ("CUSTOMER", factory.Token("CUSTOMER", userId: customerId)),
                ("SELLER", factory.Token("SELLER", userId: sellerUserId, sellerId: sellerId)),
                ("TECHNICIAN", factory.Token("TECHNICIAN", userId: technicianId)),
                ("SELLER_STAFF", factory.Token("SELLER_STAFF", userId: staffId, sellerId: sellerId)),
                ("ADMIN", factory.Token("ADMIN"))
            };

            foreach (var (role, token) in wrongRoleTokens)
            {
                using var client = factory.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                foreach (var (method, path) in endpoints)
                {
                    var req = new HttpRequestMessage(new HttpMethod(method), path);
                    if (method == "PATCH") req.Content = JsonContent.Create(new UpdateSellerStatusRequest("SUSPENDED", "Tạm ngưng kiểm tra"));
                    var resp = await client.SendAsync(req);
                    Assert.True(resp.StatusCode == HttpStatusCode.Forbidden, $"Expected 403 Forbidden for role {role} on {method} {path}, got {resp.StatusCode}");
                }
            }
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM identity.users WHERE id IN (@cid, @tid, @suid, @sfid); DELETE FROM identity.sellers WHERE id = @sid;";
            cleanup.Parameters.AddWithValue("cid", customerId);
            cleanup.Parameters.AddWithValue("tid", technicianId);
            cleanup.Parameters.AddWithValue("suid", sellerUserId);
            cleanup.Parameters.AddWithValue("sfid", staffId);
            cleanup.Parameters.AddWithValue("sid", sellerId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Validation_InvalidReason_OrPaging_OrTargetStatus_OrNotFound_ReturnsBadRequestOrNotFound()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@mid, 'mgr_val@florabot.test', 'Mgr Val', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE');
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at) VALUES(@sid, 'Validation Shop', '0901234568', 'ACTIVE', @pkg, CURRENT_DATE + 30);
            """, conn))
        {
            insert.Parameters.AddWithValue("mid", managerId);
            insert.Parameters.AddWithValue("sid", sellerId);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            // Reason too short (< 5 chars)
            var shortReason = await client.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("SUSPENDED", "Abc"));
            Assert.Equal(HttpStatusCode.BadRequest, shortReason.StatusCode);

            // Reason whitespace
            var wsReason = await client.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("SUSPENDED", "     "));
            Assert.Equal(HttpStatusCode.BadRequest, wsReason.StatusCode);

            // Reason too long (> 1000 chars)
            var longReason = await client.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("SUSPENDED", new string('x', 1001)));
            Assert.Equal(HttpStatusCode.BadRequest, longReason.StatusCode);

            // Invalid target status
            var invStatus = await client.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("INVALID_STATUS", "Lý do hợp lệ"));
            Assert.Equal(HttpStatusCode.BadRequest, invStatus.StatusCode);

            // Invalid paging on GET list
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operations/sellers?page=0")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operations/sellers?pageSize=0")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operations/sellers?pageSize=101")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/operations/sellers?status=BOGUS")).StatusCode);

            // Invalid paging on GET history
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/operations/sellers/{sellerId}/status-history?page=0")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/operations/sellers/{sellerId}/status-history?pageSize=101")).StatusCode);

            // NotFound on non-existent seller
            var nonExistent = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/operations/sellers/{nonExistent}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/operations/sellers/{nonExistent}/status-history")).StatusCode);
            var patchNotFound = await client.PatchAsJsonAsync($"/api/operations/sellers/{nonExistent}/status", new UpdateSellerStatusRequest("SUSPENDED", "Lý do hợp lệ dài hơn 5"));
            Assert.Equal(HttpStatusCode.NotFound, patchNotFound.StatusCode);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM identity.users WHERE id = @mid; DELETE FROM identity.sellers WHERE id = @sid;";
            cleanup.Parameters.AddWithValue("mid", managerId);
            cleanup.Parameters.AddWithValue("sid", sellerId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ForbiddenTransitions_AndSameState_ReturnConflict409()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var activeSeller = Guid.NewGuid();
        var suspendedSeller = Guid.NewGuid();
        var pendingSeller = Guid.NewGuid();
        var approvedSeller = Guid.NewGuid();
        var pastDueSeller = Guid.NewGuid();
        var closedSeller = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@mid, 'mgr_conflict@florabot.test', 'Mgr Conflict', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE');
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at) VALUES
            (@s_act, 'Act Shop', '0901234501', 'ACTIVE', @pkg, CURRENT_DATE + 30),
            (@s_sus, 'Sus Shop', '0901234502', 'SUSPENDED', @pkg, CURRENT_DATE + 30),
            (@s_pen, 'Pen Shop', '0901234503', 'PENDING', @pkg, CURRENT_DATE + 30),
            (@s_app, 'App Shop', '0901234504', 'APPROVED', @pkg, CURRENT_DATE + 30),
            (@s_pas, 'Pas Shop', '0901234505', 'PAST_DUE', @pkg, CURRENT_DATE + 30),
            (@s_clo, 'Clo Shop', '0901234506', 'CLOSED', @pkg, CURRENT_DATE + 30);
            """, conn))
        {
            insert.Parameters.AddWithValue("mid", managerId);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            insert.Parameters.AddWithValue("s_act", activeSeller);
            insert.Parameters.AddWithValue("s_sus", suspendedSeller);
            insert.Parameters.AddWithValue("s_pen", pendingSeller);
            insert.Parameters.AddWithValue("s_app", approvedSeller);
            insert.Parameters.AddWithValue("s_pas", pastDueSeller);
            insert.Parameters.AddWithValue("s_clo", closedSeller);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            // Same state: ACTIVE -> ACTIVE => 409
            var actToAct = await client.PatchAsJsonAsync($"/api/operations/sellers/{activeSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Thử chuyển cùng trạng thái"));
            Assert.Equal(HttpStatusCode.Conflict, actToAct.StatusCode);

            // Same state: SUSPENDED -> SUSPENDED => 409
            var susToSus = await client.PatchAsJsonAsync($"/api/operations/sellers/{suspendedSeller}/status", new UpdateSellerStatusRequest("SUSPENDED", "Thử chuyển cùng trạng thái"));
            Assert.Equal(HttpStatusCode.Conflict, susToSus.StatusCode);

            // Reject onboarding (PENDING) => 409
            var penToSus = await client.PatchAsJsonAsync($"/api/operations/sellers/{pendingSeller}/status", new UpdateSellerStatusRequest("SUSPENDED", "Chặn pending chuyển"));
            Assert.Equal(HttpStatusCode.Conflict, penToSus.StatusCode);
            var penToAct = await client.PatchAsJsonAsync($"/api/operations/sellers/{pendingSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Chặn pending kích hoạt"));
            Assert.Equal(HttpStatusCode.Conflict, penToAct.StatusCode);

            // Reject onboarding (APPROVED) => 409
            var appToSus = await client.PatchAsJsonAsync($"/api/operations/sellers/{approvedSeller}/status", new UpdateSellerStatusRequest("SUSPENDED", "Chặn approved chuyển"));
            Assert.Equal(HttpStatusCode.Conflict, appToSus.StatusCode);
            var appToAct = await client.PatchAsJsonAsync($"/api/operations/sellers/{approvedSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Chặn approved kích hoạt"));
            Assert.Equal(HttpStatusCode.Conflict, appToAct.StatusCode);

            // Reject PAST_DUE => 409
            var pasToSus = await client.PatchAsJsonAsync($"/api/operations/sellers/{pastDueSeller}/status", new UpdateSellerStatusRequest("SUSPENDED", "Chặn past due"));
            Assert.Equal(HttpStatusCode.Conflict, pasToSus.StatusCode);
            var pasToAct = await client.PatchAsJsonAsync($"/api/operations/sellers/{pastDueSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Chặn past due"));
            Assert.Equal(HttpStatusCode.Conflict, pasToAct.StatusCode);

            // Reject CLOSED => 409
            var cloToSus = await client.PatchAsJsonAsync($"/api/operations/sellers/{closedSeller}/status", new UpdateSellerStatusRequest("SUSPENDED", "Chặn closed"));
            Assert.Equal(HttpStatusCode.Conflict, cloToSus.StatusCode);
            var cloToAct = await client.PatchAsJsonAsync($"/api/operations/sellers/{closedSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Chặn closed"));
            Assert.Equal(HttpStatusCode.Conflict, cloToAct.StatusCode);

            // Reject target CLOSED or PAST_DUE => 409
            var actToClo = await client.PatchAsJsonAsync($"/api/operations/sellers/{activeSeller}/status", new UpdateSellerStatusRequest("CLOSED", "Không được đóng shop"));
            Assert.Equal(HttpStatusCode.Conflict, actToClo.StatusCode);
            var actToPas = await client.PatchAsJsonAsync($"/api/operations/sellers/{activeSeller}/status", new UpdateSellerStatusRequest("PAST_DUE", "Không được đổi sang past due"));
            Assert.Equal(HttpStatusCode.Conflict, actToPas.StatusCode);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM identity.users WHERE id = @mid; DELETE FROM identity.sellers WHERE id IN (@s_act, @s_sus, @s_pen, @s_app, @s_pas, @s_clo);";
            cleanup.Parameters.AddWithValue("mid", managerId);
            cleanup.Parameters.AddWithValue("s_act", activeSeller);
            cleanup.Parameters.AddWithValue("s_sus", suspendedSeller);
            cleanup.Parameters.AddWithValue("s_pen", pendingSeller);
            cleanup.Parameters.AddWithValue("s_app", approvedSeller);
            cleanup.Parameters.AddWithValue("s_pas", pastDueSeller);
            cleanup.Parameters.AddWithValue("s_clo", closedSeller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ExpiredOrInactivePackage_CannotResume_ReturnsConflict409()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var expiredDateSeller = Guid.NewGuid();
        var expiredTsSeller = Guid.NewGuid();
        var noPkgSeller = Guid.NewGuid();
        var inactivePkgId = Guid.NewGuid();
        var inactivePkgSeller = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@mid, 'mgr_pkg@florabot.test', 'Mgr Pkg', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE');
            INSERT INTO identity.subscription_packages(id, name, monthly_fee, annual_fee, walk_in_slots, preorder_slots, max_slots, status)
            VALUES(@inact_pkg, 'Inactive Tier', 500000, 5000000, 2, 2, 4, 'INACTIVE');

            -- 1. Expired by legacy DateOnly (package_expires_at < CURRENT_DATE, ts is null)
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at, package_expires_at_ts)
            VALUES(@s_exp_date, 'Exp Date Shop', '0901234511', 'SUSPENDED', @pkg, CURRENT_DATE - 1, NULL);

            -- 2. Expired by precise timestamp (package_expires_at_ts < app_now())
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at, package_expires_at_ts)
            VALUES(@s_exp_ts, 'Exp Ts Shop', '0901234512', 'SUSPENDED', @pkg, CURRENT_DATE + 30, public.app_now() - interval '2 hours');

            -- 3. No package
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@s_no_pkg, 'No Pkg Shop', '0901234513', 'SUSPENDED', NULL, NULL);

            -- 4. Inactive referenced package
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at, package_expires_at_ts)
            VALUES(@s_inact, 'Inact Pkg Shop', '0901234514', 'SUSPENDED', @inact_pkg, CURRENT_DATE + 30, public.app_now() + interval '30 days');
            """, conn))
        {
            insert.Parameters.AddWithValue("mid", managerId);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            insert.Parameters.AddWithValue("inact_pkg", inactivePkgId);
            insert.Parameters.AddWithValue("s_exp_date", expiredDateSeller);
            insert.Parameters.AddWithValue("s_exp_ts", expiredTsSeller);
            insert.Parameters.AddWithValue("s_no_pkg", noPkgSeller);
            insert.Parameters.AddWithValue("s_inact", inactivePkgSeller);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            // Expired date fallback cannot resume => 409
            var r1 = await client.PatchAsJsonAsync($"/api/operations/sellers/{expiredDateSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Kích hoạt lại shop gói date hết hạn"));
            Assert.Equal(HttpStatusCode.Conflict, r1.StatusCode);

            // Expired precise timestamp cannot resume => 409
            var r2 = await client.PatchAsJsonAsync($"/api/operations/sellers/{expiredTsSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Kích hoạt lại shop gói timestamp hết hạn"));
            Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);

            // No package cannot resume => 409
            var r3 = await client.PatchAsJsonAsync($"/api/operations/sellers/{noPkgSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Kích hoạt lại shop không có gói"));
            Assert.Equal(HttpStatusCode.Conflict, r3.StatusCode);

            // Inactive package cannot resume => 409
            var r4 = await client.PatchAsJsonAsync($"/api/operations/sellers/{inactivePkgSeller}/status", new UpdateSellerStatusRequest("ACTIVE", "Kích hoạt lại shop có gói bị ngưng"));
            Assert.Equal(HttpStatusCode.Conflict, r4.StatusCode);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = """
                DELETE FROM identity.users WHERE id = @mid;
                DELETE FROM identity.sellers WHERE id IN (@s_exp_date, @s_exp_ts, @s_no_pkg, @s_inact);
                DELETE FROM identity.subscription_packages WHERE id = @inact_pkg;
                """;
            cleanup.Parameters.AddWithValue("mid", managerId);
            cleanup.Parameters.AddWithValue("inact_pkg", inactivePkgId);
            cleanup.Parameters.AddWithValue("s_exp_date", expiredDateSeller);
            cleanup.Parameters.AddWithValue("s_exp_ts", expiredTsSeller);
            cleanup.Parameters.AddWithValue("s_no_pkg", noPkgSeller);
            cleanup.Parameters.AddWithValue("s_inact", inactivePkgSeller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentSameStateTransitions_OnlyOneSucceeds_OnlyOneAuditLogged_NoRoleOrFinancialChanges()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var sellerUserId = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@mid, 'mgr_race@florabot.test', 'Mgr Race', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE');
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at, package_expires_at_ts, bank_name, bank_account_no_enc, bank_holder)
            VALUES(@sid, 'Race Shop', '0901234520', 'ACTIVE', @pkg, CURRENT_DATE + 30, public.app_now() + interval '30 days', 'VietinBank', 'enc:race-bank', 'CHU RACE');
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status, seller_id)
            VALUES(@suid, 'seller_race@florabot.test', 'Seller Race User', '!hash', 'SELLER', 'ACTIVE', @sid);
            """, conn))
        {
            insert.Parameters.AddWithValue("mid", managerId);
            insert.Parameters.AddWithValue("sid", sellerId);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            insert.Parameters.AddWithValue("suid", sellerUserId);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            using var client1 = factory.CreateClient();
            client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            using var client2 = factory.CreateClient();
            client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            var task1 = client1.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("SUSPENDED", "Yêu cầu tạm ngưng 1"));
            var task2 = client2.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("SUSPENDED", "Yêu cầu tạm ngưng 2"));

            var results = await Task.WhenAll(task1, task2);
            var statusCodes = results.Select(r => r.StatusCode).ToList();

            Assert.Contains(HttpStatusCode.OK, statusCodes);
            Assert.Contains(HttpStatusCode.Conflict, statusCodes);

            // Audit verification: exactly 1 audit log created for this seller's suspension
            await using (var countAudit = new NpgsqlCommand("SELECT count(*) FROM notify.audit_logs WHERE entity_type='sellers' AND entity_id=@sid AND action='SELLER_STATUS_SUSPENDED'", conn))
            {
                countAudit.Parameters.AddWithValue("sid", sellerId);
                Assert.Equal(1L, await countAudit.ExecuteScalarAsync());
            }

            // User verification: user role and status completely unchanged
            await using (var userCheck = new NpgsqlCommand("SELECT role, status FROM identity.users WHERE id=@suid", conn))
            {
                userCheck.Parameters.AddWithValue("suid", sellerUserId);
                await using var reader = await userCheck.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("SELLER", reader.GetString(0));
                Assert.Equal("ACTIVE", reader.GetString(1));
            }

            // Financial/bank verification: bank details unchanged
            await using (var sellerCheck = new NpgsqlCommand("SELECT status, bank_name, bank_account_no_enc, bank_holder FROM identity.sellers WHERE id=@sid", conn))
            {
                sellerCheck.Parameters.AddWithValue("sid", sellerId);
                await using var reader = await sellerCheck.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("SUSPENDED", reader.GetString(0));
                Assert.Equal("VietinBank", reader.GetString(1));
                Assert.Equal("enc:race-bank", reader.GetString(2));
                Assert.Equal("CHU RACE", reader.GetString(3));
            }
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM identity.users WHERE id IN (@mid, @suid); DELETE FROM identity.sellers WHERE id = @sid;";
            cleanup.Parameters.AddWithValue("mid", managerId);
            cleanup.Parameters.AddWithValue("suid", sellerUserId);
            cleanup.Parameters.AddWithValue("sid", sellerId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task HistoryEndpoint_HandlesNullableOldReason_AndLegacyPayload()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status) VALUES(@mid, 'mgr_leg@florabot.test', 'Mgr Legacy', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE');
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at) VALUES(@sid, 'Legacy Shop', '0901234530', 'SUSPENDED', @pkg, CURRENT_DATE + 30);
            -- Insert legacy audit record where payload only contains 'from'
            INSERT INTO notify.audit_logs(actor_type, actor_id, action, entity_type, entity_id, payload)
            VALUES('USER', @mid, 'SELLER_STATUS_SUSPENDED', 'sellers', @sid, jsonb_build_object('from', 'ACTIVE'));
            """, conn))
        {
            insert.Parameters.AddWithValue("mid", managerId);
            insert.Parameters.AddWithValue("sid", sellerId);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            var historyResp = await client.GetAsync($"/api/operations/sellers/{sellerId}/status-history");
            Assert.Equal(HttpStatusCode.OK, historyResp.StatusCode);

            var history = await historyResp.Content.ReadFromJsonAsync<SellerStatusHistoryResponse>();
            Assert.NotNull(history);
            var item = Assert.Single(history.Items);
            Assert.Equal("SELLER_STATUS_SUSPENDED", item.Action);
            Assert.Equal("ACTIVE", item.FromStatus);
            Assert.Equal("SUSPENDED", item.ToStatus);
            Assert.Null(item.Reason);
            Assert.Equal(managerId, item.ActorId);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = "DELETE FROM identity.users WHERE id = @mid; DELETE FROM identity.sellers WHERE id = @sid;";
            cleanup.Parameters.AddWithValue("mid", managerId);
            cleanup.Parameters.AddWithValue("sid", sellerId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task SellerCatalogRegression_WhenSuspended_BlocksCreateAndUpdate_LeavesExistingUntouched_AuthMeStill200_ResumesCleanly()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var sellerId = Guid.NewGuid();
        var sellerUserId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status)
            VALUES(@mid, 'mgr_cat_reg@florabot.test', 'Mgr Reg', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE');

            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at, package_expires_at_ts)
            VALUES(@sid, 'Catalog Reg Shop', '0901234540', 'ACTIVE', @pkg, CURRENT_DATE + 30, public.app_now() + interval '30 days');

            INSERT INTO identity.users(id, email, full_name, password_hash, role, status, seller_id)
            VALUES(@suid, 'seller_cat_reg@florabot.test', 'Seller Reg User', '!hash', 'SELLER', 'ACTIVE', @sid);

            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, seller_id)
            VALUES(@kid, @kid::text, 'Reg Kiosk', '456 D2, Binh Thanh', 'HCM', @kid::text, @kid::text, 'ONLINE', @sid);
            """, conn))
        {
            insert.Parameters.AddWithValue("mid", managerId);
            insert.Parameters.AddWithValue("sid", sellerId);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            insert.Parameters.AddWithValue("suid", sellerUserId);
            insert.Parameters.AddWithValue("kid", kioskId);
            await insert.ExecuteNonQueryAsync();
        }

        // Create initial ARTIFICIAL product via flow.create_artificial_product
        await using (var createProd = new NpgsqlCommand("SELECT flow.create_artificial_product(@id, @seller, @actor, @name, @desc, @price, @tags, @length, @width, @height)", conn))
        {
            createProd.Parameters.AddWithValue("id", productId);
            createProd.Parameters.AddWithValue("seller", sellerId);
            createProd.Parameters.AddWithValue("actor", sellerUserId);
            createProd.Parameters.AddWithValue("name", "Hoa sáp thơm cao cấp");
            createProd.Parameters.AddWithValue("desc", "Hoa sáp trang trí");
            createProd.Parameters.AddWithValue("price", 150000L);
            createProd.Parameters.AddWithValue("tags", new[] { "RED", "ANNIVERSARY" });
            createProd.Parameters.AddWithValue("length", 15.0m);
            createProd.Parameters.AddWithValue("width", 15.0m);
            createProd.Parameters.AddWithValue("height", 25.0m);
            await createProd.ExecuteNonQueryAsync();
        }

        DateOnly initialExpiryDate;
        DateTime initialExpiryTs;
        await using (var checkSeller = new NpgsqlCommand("SELECT package_expires_at, package_expires_at_ts FROM identity.sellers WHERE id = @sid", conn))
        {
            checkSeller.Parameters.AddWithValue("sid", sellerId);
            await using var reader = await checkSeller.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            initialExpiryDate = reader.GetFieldValue<DateOnly>(0);
            initialExpiryTs = reader.GetDateTime(1);
        }

        try
        {
            using var mgrClient = factory.CreateClient();
            mgrClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            using var sellerClient = factory.CreateClient();
            sellerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("SELLER", userId: sellerUserId, sellerId: sellerId));

            // 1. Manager suspends seller
            var suspendReason = "Tạm ngưng do vi phạm quy chuẩn đóng gói hoa sáp và hoa lụa trưng bày tại kiosk";
            var suspendResp = await mgrClient.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("SUSPENDED", suspendReason));
            Assert.Equal(HttpStatusCode.OK, suspendResp.StatusCode);

            // 2. /api/auth/me still 200 for seller user
            var meResp = await sellerClient.GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);

            // 3. New catalog create blocked (403/409)
            var createNewResp = await sellerClient.PostAsJsonAsync("/api/me/products",
                new Modules.Catalog.CreateProductRequest(kioskId, "Bó hoa lụa hồng", null, 200000L, ["PINK"], 10m, 10m, 20m));
            Assert.True(createNewResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);

            // 4. Existing catalog update blocked (403/409)
            var updateExistingResp = await sellerClient.PutAsJsonAsync($"/api/sellers/{sellerId}/products/{productId}",
                new Modules.Catalog.UpdateProductRequest("Hoa sáp đổi tên", null, 180000L, ["RED"], 15m, 15m, 25m));
            Assert.True(updateExistingResp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);

            // 5. Existing product data completely untouched
            await using (var prodCheck = new NpgsqlCommand("SELECT name, price FROM catalog.flower_products WHERE id = @pid", conn))
            {
                prodCheck.Parameters.AddWithValue("pid", productId);
                await using var reader = await prodCheck.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Hoa sáp thơm cao cấp", reader.GetString(0));
                Assert.Equal(150000L, reader.GetInt64(1));
            }

            // 6. Manager resumes seller
            var resumeReason = "Đã khắc phục hoàn tất quy chuẩn đóng gói hoa sáp theo đúng thỏa thuận";
            var resumeResp = await mgrClient.PatchAsJsonAsync($"/api/operations/sellers/{sellerId}/status", new UpdateSellerStatusRequest("ACTIVE", resumeReason));
            Assert.Equal(HttpStatusCode.OK, resumeResp.StatusCode);

            // 7. Catalog update now succeeds
            var resumeUpdateResp = await sellerClient.PutAsJsonAsync($"/api/sellers/{sellerId}/products/{productId}",
                new Modules.Catalog.UpdateProductRequest("Hoa sáp thơm cao cấp v2", null, 175000L, ["RED"], 15m, 15m, 25m));
            Assert.Equal(HttpStatusCode.OK, resumeUpdateResp.StatusCode);

            // 8. New catalog create now succeeds
            var resumeCreateResp = await sellerClient.PostAsJsonAsync("/api/me/products",
                new Modules.Catalog.CreateProductRequest(kioskId, "Bó hoa lụa hồng", null, 200000L, ["PINK"], 10m, 10m, 20m));
            Assert.Equal(HttpStatusCode.Created, resumeCreateResp.StatusCode);
            var createdProductId = (await resumeCreateResp.Content.ReadFromJsonAsync<Modules.Catalog.ProductIdResponse>())!.Id;

            // 9. Seller package_id, package_expires_at, package_expires_at_ts strictly unchanged
            await using (var finalSellerCheck = new NpgsqlCommand("SELECT package_id, package_expires_at, package_expires_at_ts FROM identity.sellers WHERE id = @sid", conn))
            {
                finalSellerCheck.Parameters.AddWithValue("sid", sellerId);
                await using var reader = await finalSellerCheck.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(BasicPackageId, reader.GetGuid(0));
                Assert.Equal(initialExpiryDate, reader.GetFieldValue<DateOnly>(1));
                Assert.Equal(initialExpiryTs, reader.GetDateTime(2));
            }

            // Cleanup newly created product
            await using (var cleanNewProd = new NpgsqlCommand("DELETE FROM catalog.flower_products WHERE id = @pid", conn))
            {
                cleanNewProd.Parameters.AddWithValue("pid", createdProductId);
                await cleanNewProd.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = """
                DELETE FROM catalog.flower_products WHERE id = @pid;
                DELETE FROM kiosk_ops.kiosks WHERE id = @kid;
                DELETE FROM identity.users WHERE id IN (@mid, @suid);
                DELETE FROM identity.sellers WHERE id = @sid;
                """;
            cleanup.Parameters.AddWithValue("pid", productId);
            cleanup.Parameters.AddWithValue("kid", kioskId);
            cleanup.Parameters.AddWithValue("mid", managerId);
            cleanup.Parameters.AddWithValue("suid", sellerUserId);
            cleanup.Parameters.AddWithValue("sid", sellerId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ListPagination_HasMore_And_StatusHistoryPagination_WithTrimmedReason()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var managerId = Guid.NewGuid();
        var prefix = "PagingShop_" + Guid.NewGuid().ToString("N")[..6];
        var seller1 = Guid.NewGuid();
        var seller2 = Guid.NewGuid();
        var seller3 = Guid.NewGuid();

        await using var conn = await data.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.users(id, email, full_name, password_hash, role, status)
            VALUES(@mid, 'mgr_page@florabot.test', 'Mgr Page', '!hash', 'OPERATIONS_MANAGER', 'ACTIVE');

            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at, created_at)
            VALUES
            (@s1, @pfx || '_1', '0901234551', 'ACTIVE', @pkg, CURRENT_DATE + 30, public.app_now() - interval '3 minutes'),
            (@s2, @pfx || '_2', '0901234552', 'ACTIVE', @pkg, CURRENT_DATE + 30, public.app_now() - interval '2 minutes'),
            (@s3, @pfx || '_3', '0901234553', 'ACTIVE', @pkg, CURRENT_DATE + 30, public.app_now() - interval '1 minute');
            """, conn))
        {
            insert.Parameters.AddWithValue("mid", managerId);
            insert.Parameters.AddWithValue("pfx", prefix);
            insert.Parameters.AddWithValue("pkg", BasicPackageId);
            insert.Parameters.AddWithValue("s1", seller1);
            insert.Parameters.AddWithValue("s2", seller2);
            insert.Parameters.AddWithValue("s3", seller3);
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.Token("OPERATIONS_MANAGER", userId: managerId));

            // List Page 1 with pageSize=2 -> HasMore should be true
            var p1Resp = await client.GetAsync($"/api/operations/sellers?search={prefix}&page=1&pageSize=2");
            Assert.Equal(HttpStatusCode.OK, p1Resp.StatusCode);
            var page1 = await p1Resp.Content.ReadFromJsonAsync<OperationsSellerPage>();
            Assert.NotNull(page1);
            Assert.Equal(2, page1.Items.Count);
            Assert.True(page1.HasMore);

            // List Page 2 with pageSize=2 -> HasMore should be false
            var p2Resp = await client.GetAsync($"/api/operations/sellers?search={prefix}&page=2&pageSize=2");
            Assert.Equal(HttpStatusCode.OK, p2Resp.StatusCode);
            var page2 = await p2Resp.Content.ReadFromJsonAsync<OperationsSellerPage>();
            Assert.NotNull(page2);
            Assert.Single(page2.Items);
            Assert.False(page2.HasMore);

            // Status history pagination + trimmed reason test
            // Suspend with leading/trailing spaces in reason
            var untrimmedReason = "   Tạm ngưng lý do có khoảng trắng đầu cuối để kiểm tra trim   ";
            var patchResp = await client.PatchAsJsonAsync($"/api/operations/sellers/{seller1}/status",
                new UpdateSellerStatusRequest("SUSPENDED", untrimmedReason));
            Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);

            // Reactivate with another reason to produce 2 history records
            var reactivateReason = "Kích hoạt lại lần hai để test phân trang lịch sử";
            var patchResp2 = await client.PatchAsJsonAsync($"/api/operations/sellers/{seller1}/status",
                new UpdateSellerStatusRequest("ACTIVE", reactivateReason));
            Assert.Equal(HttpStatusCode.OK, patchResp2.StatusCode);

            // History page 1 (pageSize=1) -> HasMore true, latest record has reactivateReason
            var h1Resp = await client.GetAsync($"/api/operations/sellers/{seller1}/status-history?page=1&pageSize=1");
            Assert.Equal(HttpStatusCode.OK, h1Resp.StatusCode);
            var hPage1 = await h1Resp.Content.ReadFromJsonAsync<SellerStatusHistoryResponse>();
            Assert.NotNull(hPage1);
            Assert.Single(hPage1.Items);
            Assert.True(hPage1.HasMore);
            Assert.Equal("SELLER_STATUS_ACTIVE", hPage1.Items[0].Action);
            Assert.Equal(reactivateReason, hPage1.Items[0].Reason);

            // History page 2 (pageSize=1) -> HasMore false, older record has trimmed reason
            var h2Resp = await client.GetAsync($"/api/operations/sellers/{seller1}/status-history?page=2&pageSize=1");
            Assert.Equal(HttpStatusCode.OK, h2Resp.StatusCode);
            var hPage2 = await h2Resp.Content.ReadFromJsonAsync<SellerStatusHistoryResponse>();
            Assert.NotNull(hPage2);
            Assert.Single(hPage2.Items);
            Assert.False(hPage2.HasMore);
            Assert.Equal("SELLER_STATUS_SUSPENDED", hPage2.Items[0].Action);
            Assert.Equal("Tạm ngưng lý do có khoảng trắng đầu cuối để kiểm tra trim", hPage2.Items[0].Reason);
        }
        finally
        {
            await using var cleanup = conn.CreateCommand();
            cleanup.CommandText = """
                DELETE FROM identity.users WHERE id = @mid;
                DELETE FROM identity.sellers WHERE id IN (@s1, @s2, @s3);
                """;
            cleanup.Parameters.AddWithValue("mid", managerId);
            cleanup.Parameters.AddWithValue("s1", seller1);
            cleanup.Parameters.AddWithValue("s2", seller2);
            cleanup.Parameters.AddWithValue("s3", seller3);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void OpenApi_FourRoutes_HaveExpectedMetadataAndResponseSchemas()
    {
        var dataSource = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var endpoints = dataSource.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().ToList();

        var routeSellers = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/api/operations/sellers" &&
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        var routeDetail = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/api/operations/sellers/{sellerId:guid}" &&
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);
        var routeStatus = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/api/operations/sellers/{sellerId:guid}/status" &&
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains("PATCH") == true);
        var routeHistory = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/api/operations/sellers/{sellerId:guid}/status-history" &&
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);

        foreach (var route in new[] { routeSellers, routeDetail, routeStatus, routeHistory })
        {
            var auth = route.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>();
            Assert.NotNull(auth);
            Assert.Equal("OperationsManager", auth.Policy);

            var tags = route.Metadata.GetMetadata<Microsoft.AspNetCore.Http.TagsAttribute>();
            Assert.NotNull(tags);
            Assert.Contains("Identity", tags.Tags);

            var summary = route.Metadata.GetMetadata<Microsoft.AspNetCore.Http.EndpointSummaryAttribute>();
            Assert.NotNull(summary);
            Assert.False(string.IsNullOrWhiteSpace(summary.Summary));
        }

        var sellersOk = Assert.Single(routeSellers.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>(), m => m.StatusCode == 200);
        Assert.Equal(typeof(OperationsSellerPage), sellersOk.Type);

        var detailOk = Assert.Single(routeDetail.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>(), m => m.StatusCode == 200);
        Assert.Equal(typeof(OperationsSellerDetail), detailOk.Type);

        var statusOk = Assert.Single(routeStatus.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>(), m => m.StatusCode == 200);
        Assert.Equal(typeof(OperationsSellerDetail), statusOk.Type);
        Assert.Contains(routeStatus.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>(), m => m.StatusCode == 409);

        var historyOk = Assert.Single(routeHistory.Metadata.OfType<Microsoft.AspNetCore.Http.Metadata.IProducesResponseTypeMetadata>(), m => m.StatusCode == 200);
        Assert.Equal(typeof(SellerStatusHistoryResponse), historyOk.Type);
    }

    private static void AssertNoSecretFields(string json)
    {
        using var doc = JsonDocument.Parse(json);
        CheckElement(doc.RootElement);

        static void CheckElement(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        var name = prop.Name.ToLowerInvariant();
                        Assert.False(name.Contains("bank") || name.Contains("password") || name.Contains("secret") || name.Contains("devicekey"),
                            $"Disallowed secret property '{prop.Name}' found in response DTO");
                        CheckElement(prop.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        CheckElement(item);
                    }
                    break;
            }
        }
    }
}
