using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class DiscoveryCatalogTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private NpgsqlDataSource DataSource => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private static string HashKey(string rawKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

    private async Task SeedSellerAsync(Guid sellerId, Guid userId, string status = "ACTIVE", int packageDays = 30)
    {
        await using var pkgCmd = DataSource.CreateCommand(@"
            INSERT INTO identity.subscription_packages (id, name, monthly_fee, max_slots, status)
            VALUES('20000000-0000-0000-0000-000000000099', 'Large Catalog Test Package', 10000000, 100, 'ACTIVE')
            ON CONFLICT (id) DO UPDATE SET max_slots = 100, status = 'ACTIVE';
        ");
        await pkgCmd.ExecuteNonQueryAsync();

        await using var cmd = DataSource.CreateCommand(@"
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@sellerId, @name, @phone, @status, '20000000-0000-0000-0000-000000000099', CURRENT_DATE + @pkgDays)
            ON CONFLICT (id) DO UPDATE SET
                status = EXCLUDED.status,
                package_expires_at = EXCLUDED.package_expires_at;
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id)
            VALUES(@userId, @email, '!unprovisioned', 'Discovery Seller User', 'SELLER', 'ACTIVE', @sellerId)
            ON CONFLICT (id) DO UPDATE SET
                role = 'SELLER',
                status = 'ACTIVE',
                seller_id = @sellerId;
        ");
        cmd.Parameters.AddWithValue("sellerId", sellerId);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("name", $"Shop {sellerId:N}"[..20]);
        cmd.Parameters.AddWithValue("phone", $"09{Random.Shared.Next(10000000, 99999999)}");
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("pkgDays", packageDays);
        cmd.Parameters.AddWithValue("email", $"seller-{userId:N}@example.invalid");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedKioskAsync(Guid kioskId, string code, string name, string address, string region, string status, double? lat, double? lon, string? apiKey = null, Guid? sellerId = null)
    {
        var keyHash = apiKey is not null ? HashKey(apiKey) : null;
        await using var cmd = DataSource.CreateCommand(@"
            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, latitude, longitude, api_key_hash, seller_id, created_at, updated_at)
            VALUES(@id, @code, @name, @address, @region, @hw, @mqtt, @status, @lat, @lon, @keyHash, @sellerId, public.app_now(), public.app_now())
            ON CONFLICT (id) DO UPDATE SET
                address = EXCLUDED.address,
                status = EXCLUDED.status,
                latitude = EXCLUDED.latitude,
                longitude = EXCLUDED.longitude,
                api_key_hash = EXCLUDED.api_key_hash,
                seller_id = COALESCE(EXCLUDED.seller_id, kiosk_ops.kiosks.seller_id),
                updated_at = public.app_now();
        ");
        cmd.Parameters.AddWithValue("id", kioskId);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("address", address);
        cmd.Parameters.AddWithValue("region", region);
        cmd.Parameters.AddWithValue("hw", $"hw-{kioskId:N}");
        cmd.Parameters.AddWithValue("mqtt", $"mqtt-{kioskId:N}");
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("lat", (object?)lat ?? DBNull.Value);
        cmd.Parameters.AddWithValue("lon", (object?)lon ?? DBNull.Value);
        cmd.Parameters.AddWithValue("keyHash", (object?)keyHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sellerId", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)sellerId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedSlotAsync(Guid slotId, Guid kioskId, string slotCode, short relayChannel = 0, string? zone = null)
    {
        await using var cmd = DataSource.CreateCommand(@"
            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, status, current_seller_id, relay_channel, zone, created_at, updated_at)
            VALUES(@id, @kioskId, @slotCode, 'FREE', NULL, @relay, @zone, public.app_now(), public.app_now())
            ON CONFLICT (id) DO UPDATE SET
                relay_channel = EXCLUDED.relay_channel,
                zone = EXCLUDED.zone,
                updated_at = public.app_now();
        ");
        cmd.Parameters.AddWithValue("id", slotId);
        cmd.Parameters.AddWithValue("kioskId", kioskId);
        cmd.Parameters.AddWithValue("slotCode", slotCode);
        cmd.Parameters.AddWithValue("relay", relayChannel);
        cmd.Parameters.AddWithValue("zone", (object?)zone ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task AssignKioskAsync(Guid kioskId, Guid sellerId)
    {
        await using var cmd = DataSource.CreateCommand(
            "SELECT flow.assign_kiosk(@kiosk, @seller, '10000000-0000-0000-0000-000000000001')");
        cmd.Parameters.AddWithValue("kiosk", kioskId);
        cmd.Parameters.AddWithValue("seller", sellerId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedProductAsync(Guid productId, Guid sellerId, string name, long price, string status = "ACTIVE")
    {
        await using var cmd = DataSource.CreateCommand(@"
            SELECT flow.create_artificial_product(
                @id, @sellerId,
                (SELECT id FROM identity.users WHERE seller_id=@sellerId AND role='SELLER' AND status='ACTIVE' ORDER BY id LIMIT 1),
                @name, 'Artificial handcrafted flowers', @price, ARRAY['ARTIFICIAL','DECORATION'], 10, 10, 10);
            UPDATE catalog.flower_products SET status=@status WHERE id=@id;
        ");
        cmd.Parameters.AddWithValue("id", productId);
        cmd.Parameters.AddWithValue("sellerId", sellerId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("price", price);
        cmd.Parameters.AddWithValue("status", status);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> StockBouquetAsync(Guid productId, Guid slotId, Guid staffUserId, string qrCode)
    {
        await using var cmd = DataSource.CreateCommand(
            "SELECT flow.stock_bouquet(flow.new_batch(), @product, @slot, @actor, @qr)");
        cmd.Parameters.AddWithValue("product", productId);
        cmd.Parameters.AddWithValue("slot", slotId);
        cmd.Parameters.AddWithValue("actor", staffUserId);
        cmd.Parameters.AddWithValue("qr", qrCode);
        var result = await cmd.ExecuteScalarAsync();
        return (Guid)result!;
    }

    // =========================================================================
    // 1. /api/kiosks Privacy and Listing Tests
    // =========================================================================

    [Fact]
    public void PublicKiosksDto_ExcludesSensitiveHardwareAndCredentialProperties()
    {
        var publicProps = typeof(PublicKioskDto).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var bannedPrefixes = new[] { "ApiKey", "HardwareId", "MqttClientId", "SellerId", "Password", "Secret", "Token", "Credential" };

        foreach (var prop in publicProps)
        {
            Assert.DoesNotContain(bannedPrefixes, prefix => prop.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase));
        }

        var propNames = publicProps.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Id", propNames);
        Assert.Contains("Code", propNames);
        Assert.Contains("Name", propNames);
        Assert.Contains("Address", propNames);
        Assert.Contains("Region", propNames);
        Assert.Contains("Status", propNames);
        Assert.Contains("Latitude", propNames);
        Assert.Contains("Longitude", propNames);
        Assert.Contains("LocationComplete", propNames);
    }

    [Fact]
    public async Task PublicKiosksEndpoint_ReturnsNonDisabledKiosksWithCoordinatesAndAnonymity()
    {
        var kOnline = Guid.NewGuid();
        var kOffline = Guid.NewGuid();
        var kDisabled = Guid.NewGuid();

        await SeedKioskAsync(kOnline, $"K-PUB-ON-{kOnline:N}"[..18], "Online Kiosk", "100 Le Duan, D1", "HCM", "ONLINE", 10.779, 106.699);
        await SeedKioskAsync(kOffline, $"K-PUB-OFF-{kOffline:N}"[..18], "Offline Kiosk", "200 Hai Ba Trung, D1", "HCM", "OFFLINE", 10.785, 106.695);
        await SeedKioskAsync(kDisabled, $"K-PUB-DIS-{kDisabled:N}"[..18], "Disabled Kiosk", "300 Vo Van Kiet, D1", "HCM", "DISABLED", 10.760, 106.680);

        using var client = factory.CreateClient();
        var res = await client.GetAsync("/api/kiosks");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var kiosks = await res.Content.ReadFromJsonAsync<List<PublicKioskDto>>();
        Assert.NotNull(kiosks);

        Assert.Contains(kiosks, k => k.Id == kOnline && k.Status == "ONLINE" && k.LocationComplete);
        Assert.Contains(kiosks, k => k.Id == kOffline && k.Status == "OFFLINE" && k.LocationComplete);
        Assert.DoesNotContain(kiosks, k => k.Id == kDisabled);

        // Raw json privacy audit: ensure sensitive fields are absent from response
        var rawJson = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hardware_id", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hardwareId", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mqtt_client_id", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mqttClientId", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key_hash", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seller_id", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sellerId", rawJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicKiosksOpenApi_ExportsOnlyPublicResponseFields()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("ADMIN"));
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schema = document.RootElement.GetProperty("paths").GetProperty("/api/kiosks")
            .GetProperty("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("array", schema.GetProperty("type").GetString());
        var reference = schema.GetProperty("items").GetProperty("$ref").GetString()!;
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(reference.Split('/')[^1]).GetProperty("properties");
        var names = properties.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "address", "code", "id", "latitude", "locationComplete", "longitude", "name", "region", "status" }, names);
    }

    // =========================================================================
    // 2. /api/shop/catalog (Web Catalog) Tests
    // =========================================================================

    [Fact]
    public void WebCatalogDto_ExcludesSensitiveCredentialsAndContactFields()
    {
        var itemProps = typeof(WebCatalogItem).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var bannedNames = new[] { "ApiKey", "HardwareId", "MqttClientId", "Password", "Secret", "Token", "Phone", "Email" };

        foreach (var prop in itemProps)
        {
            Assert.DoesNotContain(bannedNames, banned => prop.Name.Contains(banned, StringComparison.OrdinalIgnoreCase));
        }

        var propNames = itemProps.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ProductId", propNames);
        Assert.Contains("SellerId", propNames);
        Assert.Contains("ShopName", propNames);
        Assert.Contains("Name", propNames);
        Assert.Contains("Description", propNames);
        Assert.Contains("Price", propNames);
        Assert.Contains("KioskId", propNames);
        Assert.Contains("KioskName", propNames);
        Assert.Contains("Address", propNames);
        Assert.Contains("Photo", propNames);
        Assert.Contains("Bouquets", propNames);
    }

    [Fact]
    public async Task WebCatalogEndpoint_ReturnsStockedPreorderBouquetsWithSearchAndPagination()
    {
        var sellerId = Guid.NewGuid();
        var sellerUserId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();

        await SeedSellerAsync(sellerId, sellerUserId);
        await SeedKioskAsync(kioskId, $"K-CAT-{kioskId:N}"[..18], "Kiosk Hoa Vai", "15 Le Loi, D1", "HCM", "ONLINE", 10.7719, 106.6983);

        // Pagination concerns product offerings, not the physical capacity of a kiosk.
        var preorderSlotId = Guid.NewGuid();
        await SeedSlotAsync(preorderSlotId, kioskId, "P01", 0, zone: "PREORDER");
        var walkInSlotId = Guid.NewGuid();
        await SeedSlotAsync(walkInSlotId, kioskId, "W01", 1, zone: "WALK_IN");

        await AssignKioskAsync(kioskId, sellerId);

        // Create 26 ACTIVE ARTIFICIAL products with unique ASCII prefix for deterministic ordering & isolated search
        var asciiPrefix = $"PAGE{Guid.NewGuid():N}_";
        var productIds = new List<Guid>();
        var preorderBouquetIds = new List<Guid>();

        for (int i = 1; i <= 26; i++)
        {
            var prodId = Guid.NewGuid();
            productIds.Add(prodId);
            var prodName = $"{asciiPrefix}{i:D2}";
            await SeedProductAsync(prodId, sellerId, prodName, 100000 + i * 1000);
        }
        preorderBouquetIds.Add(await StockBouquetAsync(productIds[0], preorderSlotId, sellerUserId, $"QR-PAGE-{Guid.NewGuid():N}"[..20]));

        // Also stock 1 walk-in bouquet for product 1 to verify walk-in is NOT in web catalog bouquets
        var bqWalkIn = await StockBouquetAsync(productIds[0], walkInSlotId, sellerUserId, $"QR-PAGE-W-{Guid.NewGuid():N}"[..20]);

        using var client = factory.CreateClient();

        // 1. Page 1: LIMIT 26 OFFSET 0 -> returns first 25 items, HasMore = true
        var page1Res = await client.GetAsync($"/api/shop/catalog?page=1&search={asciiPrefix}");
        Assert.Equal(HttpStatusCode.OK, page1Res.StatusCode);
        var page1 = await page1Res.Content.ReadFromJsonAsync<WebCatalogPage>();
        Assert.NotNull(page1);
        Assert.Equal(1, page1.Page);
        Assert.True(page1.HasMore, "Page 1 must indicate HasMore = true when 26 matching items exist");
        Assert.Equal(25, page1.Items.Count);

        // Verify deterministic name ordering on Page 1 (_01 to _25)
        for (int i = 0; i < 25; i++)
        {
            var expectedName = $"{asciiPrefix}{i + 1:D2}";
            Assert.Equal(expectedName, page1.Items[i].Name);
            Assert.Equal(productIds[i], page1.Items[i].ProductId);
            Assert.Equal(kioskId, page1.Items[i].KioskId);
        }

        // Verify PREORDER separation: Product 1 has preorder bouquet, but NOT walk-in bouquet
        var item1 = page1.Items[0];
        Assert.Contains(preorderBouquetIds[0], item1.Bouquets);
        Assert.DoesNotContain(bqWalkIn, item1.Bouquets);

        // 2. Page 2: LIMIT 26 OFFSET 25 -> returns remaining 1 item, HasMore = false
        var page2Res = await client.GetAsync($"/api/shop/catalog?page=2&search={asciiPrefix}");
        Assert.Equal(HttpStatusCode.OK, page2Res.StatusCode);
        var page2 = await page2Res.Content.ReadFromJsonAsync<WebCatalogPage>();
        Assert.NotNull(page2);
        Assert.Equal(2, page2.Page);
        Assert.False(page2.HasMore, "Page 2 must indicate HasMore = false when exactly 1 remaining item exists");
        var lastItem = Assert.Single(page2.Items);
        Assert.Equal($"{asciiPrefix}26", lastItem.Name);
        Assert.Equal(productIds[25], lastItem.ProductId);

        // 3. Verify exactly 26 distinct IDs across Page 1 and Page 2
        var allFetchedIds = page1.Items.Select(x => x.ProductId).Concat(page2.Items.Select(x => x.ProductId)).ToList();
        Assert.Equal(26, allFetchedIds.Count);
        Assert.Equal(26, allFetchedIds.Distinct().Count());
        Assert.Equal(productIds, allFetchedIds);

        // 4. Input validation bounds: page 0, negative page, page 100001 (beyond max 100000), search > 100 chars
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/shop/catalog?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/shop/catalog?page=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/shop/catalog?page=100001")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/shop/catalog?search={new string('a', 101)}")).StatusCode);

        // 5. Search with non-matching term returns empty list and HasMore = false
        var emptyRes = await client.GetAsync($"/api/shop/catalog?search={asciiPrefix}NONEXISTENT");
        Assert.Equal(HttpStatusCode.OK, emptyRes.StatusCode);
        var emptyPage = await emptyRes.Content.ReadFromJsonAsync<WebCatalogPage>();
        Assert.NotNull(emptyPage);
        Assert.Empty(emptyPage.Items);
        Assert.False(emptyPage.HasMore);

        // 6. Privacy audit on json payload: no phone, password, apiKey
        var rawJson = await page1Res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("phone", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", rawJson, StringComparison.OrdinalIgnoreCase);

        await using var hideProduct = DataSource.CreateCommand("UPDATE catalog.flower_products SET status='INACTIVE' WHERE id=@id");
        hideProduct.Parameters.AddWithValue("id", productIds[0]);
        await hideProduct.ExecuteNonQueryAsync();
        var inactivePage = (await client.GetFromJsonAsync<WebCatalogPage>($"/api/shop/catalog?search={asciiPrefix}"))!;
        Assert.DoesNotContain(inactivePage.Items, x => x.ProductId == productIds[0]);
        Assert.Equal(25, inactivePage.Items.Count);

        await using var suspendSeller = DataSource.CreateCommand("UPDATE identity.sellers SET status='SUSPENDED' WHERE id=@id");
        suspendSeller.Parameters.AddWithValue("id", sellerId);
        await suspendSeller.ExecuteNonQueryAsync();
        Assert.Empty((await client.GetFromJsonAsync<WebCatalogPage>($"/api/shop/catalog?search={asciiPrefix}"))!.Items);

        await using var expirePackage = DataSource.CreateCommand("UPDATE identity.sellers SET status='ACTIVE',package_expires_at=CURRENT_DATE,package_expires_at_ts=NULL WHERE id=@id");
        expirePackage.Parameters.AddWithValue("id", sellerId);
        await expirePackage.ExecuteNonQueryAsync();
        Assert.Empty((await client.GetFromJsonAsync<WebCatalogPage>($"/api/shop/catalog?search={asciiPrefix}"))!.Items);
    }

    [Fact]
    public async Task WebCatalogEndpoint_ExcludesIncompleteKiosks()
    {
        var sellerActive = Guid.NewGuid();
        var sellerUserActive = Guid.NewGuid();
        var kioskIncomplete = Guid.NewGuid();
        var slotInc = Guid.NewGuid();
        var productInc = Guid.NewGuid();

        await SeedSellerAsync(sellerActive, sellerUserActive);

        // Incomplete kiosk with NULL coordinates must be excluded from web catalog
        await SeedKioskAsync(kioskIncomplete, $"K-NO-PIN-{kioskIncomplete:N}"[..18], "Incomplete Kiosk", "No Pin Road", "HCM", "ONLINE", null, null);
        await SeedSlotAsync(slotInc, kioskIncomplete, "C01", 0, zone: "PREORDER");
        await AssignKioskAsync(kioskIncomplete, sellerActive);

        var incProdName = $"Hoa Giay C01 {Guid.NewGuid():N}"[..20];
        await SeedProductAsync(productInc, sellerActive, incProdName, 200000);
        await StockBouquetAsync(productInc, slotInc, sellerUserActive, $"QR-INC-{Guid.NewGuid():N}"[..20]);

        using var client = factory.CreateClient();
        var res = await client.GetAsync($"/api/shop/catalog?search={Uri.EscapeDataString(incProdName)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var page = await res.Content.ReadFromJsonAsync<WebCatalogPage>();
        Assert.NotNull(page);
        Assert.DoesNotContain(page.Items, x => x.ProductId == productInc);
    }

    // =========================================================================
    // 3. /api/kiosks/{id}/catalog and /catalog/items Tests
    // =========================================================================

    [Fact]
    public async Task KioskCatalog_RejectsUnauthenticatedInvalidKeyForeignAndDisabledKiosk()
    {
        var kioskId = Guid.NewGuid();
        var otherKioskId = Guid.NewGuid();
        var disabledKioskId = Guid.NewGuid();
        var validKey = $"device-key-alpha-{Guid.NewGuid():N}";
        var otherKey = $"device-key-beta-{Guid.NewGuid():N}";
        var disabledKey = $"device-key-dis-{Guid.NewGuid():N}";

        await SeedKioskAsync(kioskId, $"K-DEV-1-{kioskId:N}"[..18], "Kiosk Alpha", "1 Nguyen Hue", "HCM", "ONLINE", 10.77, 106.69, apiKey: validKey);
        await SeedKioskAsync(otherKioskId, $"K-DEV-2-{otherKioskId:N}"[..18], "Kiosk Beta", "2 Nguyen Hue", "HCM", "ONLINE", 10.78, 106.70, apiKey: otherKey);
        await SeedKioskAsync(disabledKioskId, $"K-DEV-3-{disabledKioskId:N}"[..18], "Kiosk Dis", "3 Nguyen Hue", "HCM", "DISABLED", 10.79, 106.71, apiKey: disabledKey);

        var catalogUrl = $"/api/kiosks/{kioskId}/catalog";
        var itemsUrl = $"/api/kiosks/{kioskId}/catalog/items";

        // 1. Unauthenticated (no header) -> 401 Unauthorized
        using var anonClient = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync(catalogUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonClient.GetAsync(itemsUrl)).StatusCode);

        // 2. Short key (< 8 chars) -> 401 Unauthorized
        using var shortKeyClient = factory.CreateClient();
        shortKeyClient.DefaultRequestHeaders.Add("X-Kiosk-Key", "short");
        Assert.Equal(HttpStatusCode.Unauthorized, (await shortKeyClient.GetAsync(catalogUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await shortKeyClient.GetAsync(itemsUrl)).StatusCode);

        // 3. Wrong key -> 401 Unauthorized
        using var wrongKeyClient = factory.CreateClient();
        wrongKeyClient.DefaultRequestHeaders.Add("X-Kiosk-Key", "wrong-device-key-nonexistent-123");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKeyClient.GetAsync(catalogUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongKeyClient.GetAsync(itemsUrl)).StatusCode);

        // 4. Disabled kiosk key -> 401 Unauthorized (FindEnabledAsync enforces status != 'DISABLED')
        using var disKeyClient = factory.CreateClient();
        disKeyClient.DefaultRequestHeaders.Add("X-Kiosk-Key", disabledKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await disKeyClient.GetAsync($"/api/kiosks/{disabledKioskId}/catalog")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await disKeyClient.GetAsync($"/api/kiosks/{disabledKioskId}/catalog/items")).StatusCode);

        // 5. Foreign kiosk key: Kiosk Beta attempts to access Kiosk Alpha's catalog -> 403 Forbidden
        using var foreignClient = factory.CreateClient();
        foreignClient.DefaultRequestHeaders.Add("X-Kiosk-Key", otherKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreignClient.GetAsync(catalogUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreignClient.GetAsync(itemsUrl)).StatusCode);

        // 6. Token without kiosk_id claim (e.g. human admin/seller/customer without device binding) -> 403 Forbidden
        using var humanSellerClient = factory.CreateClient();
        humanSellerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER"));
        Assert.Equal(HttpStatusCode.Forbidden, (await humanSellerClient.GetAsync(catalogUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await humanSellerClient.GetAsync(itemsUrl)).StatusCode);
    }

    [Fact]
    public async Task KioskCatalog_DeviceBoundKeySucceedsAndOnlyReturnsWalkInStockedBouquets()
    {
        var sellerId = Guid.NewGuid();
        var sellerUserId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();
        var slotWalkIn = Guid.NewGuid();
        var slotPreorder = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var rawApiKey = $"device-key-walkin-{Guid.NewGuid():N}";

        await SeedSellerAsync(sellerId, sellerUserId);
        await SeedKioskAsync(kioskId, $"K-DEV-W-{kioskId:N}"[..18], "Kiosk Sales", "50 Pasteur, D1", "HCM", "ONLINE", 10.775, 106.700, apiKey: rawApiKey);
        await SeedSlotAsync(slotWalkIn, kioskId, "W01", 0, zone: "WALK_IN");
        await SeedSlotAsync(slotPreorder, kioskId, "P01", 1, zone: "PREORDER");
        await AssignKioskAsync(kioskId, sellerId);

        var productName = $"Hoa Lua Nghe Thuat {Guid.NewGuid():N}"[..25];
        await SeedProductAsync(productId, sellerId, productName, 320000);

        var bkW = await StockBouquetAsync(productId, slotWalkIn, sellerUserId, $"QR-WK-{Guid.NewGuid():N}"[..20]);
        var bkP = await StockBouquetAsync(productId, slotPreorder, sellerUserId, $"QR-PK-{Guid.NewGuid():N}"[..20]);

        using var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Add("X-Kiosk-Key", rawApiKey);

        // 1. GET /api/kiosks/{kioskId}/catalog
        var catRes = await deviceClient.GetAsync($"/api/kiosks/{kioskId}/catalog");
        Assert.Equal(HttpStatusCode.OK, catRes.StatusCode);
        var catJson = await catRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(catJson);
        var elements = doc.RootElement.EnumerateArray().ToList();

        // Must contain WALK_IN bouquet
        Assert.Contains(elements, el => el.GetProperty("bouquet_id").GetGuid() == bkW);
        // Must EXCLUDE PREORDER reserved bouquet
        Assert.DoesNotContain(elements, el => el.GetProperty("bouquet_id").GetGuid() == bkP);

        // 2. GET /api/kiosks/{kioskId}/catalog/items
        var itemsRes = await deviceClient.GetAsync($"/api/kiosks/{kioskId}/catalog/items");
        Assert.Equal(HttpStatusCode.OK, itemsRes.StatusCode);
        Assert.Equal("no-store", itemsRes.Headers.CacheControl?.ToString());

        var catalogItems = await itemsRes.Content.ReadFromJsonAsync<List<KioskCatalogResponse>>();
        Assert.NotNull(catalogItems);

        var walkInItem = Assert.Single(catalogItems, x => x.BouquetId == bkW);
        Assert.Equal(productName, walkInItem.Name);
        Assert.Equal(320000, walkInItem.Price);
        Assert.Equal("W01", walkInItem.SlotCode);

        // Excludes preorder bouquet
        Assert.DoesNotContain(catalogItems, x => x.BouquetId == bkP);
    }

    [Fact]
    public async Task KioskCatalog_ExcludesSuspendedAndExpiredSellerEntitlements()
    {
        var sellerId = Guid.NewGuid();
        var sellerUserId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();
        var slotId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var rawApiKey = $"device-key-lease-{Guid.NewGuid():N}";

        await SeedSellerAsync(sellerId, sellerUserId);
        await SeedKioskAsync(kioskId, $"K-EXP-{kioskId:N}"[..18], "Expiry Kiosk", "88 Ham Nghi", "HCM", "ONLINE", 10.772, 106.702, apiKey: rawApiKey);
        await SeedSlotAsync(slotId, kioskId, "E01", 0, zone: "WALK_IN");
        await AssignKioskAsync(kioskId, sellerId);

        var prodName = $"Hoa Giay Exp {Guid.NewGuid():N}"[..20];
        await SeedProductAsync(productId, sellerId, prodName, 150000);
        var bqId = await StockBouquetAsync(productId, slotId, sellerUserId, $"QR-EXP-{Guid.NewGuid():N}"[..20]);

        // Assert stock sellable_until is infinity (ARTIFICIAL products never expire)
        await using (var checkCmd = DataSource.CreateCommand("SELECT sellable_until = 'infinity'::timestamptz FROM kiosk_ops.bouquets WHERE id = @id"))
        {
            checkCmd.Parameters.AddWithValue("id", bqId);
            var isInfinity = (bool)(await checkCmd.ExecuteScalarAsync())!;
            Assert.True(isInfinity, "ARTIFICIAL bouquets must have sellable_until = infinity");
        }

        using var deviceClient = factory.CreateClient();
        deviceClient.DefaultRequestHeaders.Add("X-Kiosk-Key", rawApiKey);

        // Initially present
        var initRes = await deviceClient.GetAsync($"/api/kiosks/{kioskId}/catalog/items");
        Assert.Equal(HttpStatusCode.OK, initRes.StatusCode);
        var initItems = await initRes.Content.ReadFromJsonAsync<List<KioskCatalogResponse>>();
        Assert.Contains(initItems!, x => x.BouquetId == bqId);

        // 1. Seller SUSPENDED exclusion
        await using (var suspendCmd = DataSource.CreateCommand("UPDATE identity.sellers SET status = 'SUSPENDED' WHERE id = @seller"))
        {
            suspendCmd.Parameters.AddWithValue("seller", sellerId);
            await suspendCmd.ExecuteNonQueryAsync();
        }

        var suspendedRes = await deviceClient.GetAsync($"/api/kiosks/{kioskId}/catalog/items");
        Assert.Equal(HttpStatusCode.OK, suspendedRes.StatusCode);
        var suspendedItems = await suspendedRes.Content.ReadFromJsonAsync<List<KioskCatalogResponse>>();
        Assert.DoesNotContain(suspendedItems!, x => x.BouquetId == bqId);

        // 2. Seller ACTIVE but package expired (package_expires_at = CURRENT_DATE, package_expires_at_ts = NULL)
        await using (var expirePkgCmd = DataSource.CreateCommand(@"
            UPDATE identity.sellers
            SET status = 'ACTIVE', package_expires_at = CURRENT_DATE, package_expires_at_ts = NULL
            WHERE id = @seller;
        "))
        {
            expirePkgCmd.Parameters.AddWithValue("seller", sellerId);
            await expirePkgCmd.ExecuteNonQueryAsync();
        }

        var expiredPkgRes = await deviceClient.GetAsync($"/api/kiosks/{kioskId}/catalog/items");
        Assert.Equal(HttpStatusCode.OK, expiredPkgRes.StatusCode);
        var expiredPkgItems = await expiredPkgRes.Content.ReadFromJsonAsync<List<KioskCatalogResponse>>();
        Assert.DoesNotContain(expiredPkgItems!, x => x.BouquetId == bqId);
    }
}
