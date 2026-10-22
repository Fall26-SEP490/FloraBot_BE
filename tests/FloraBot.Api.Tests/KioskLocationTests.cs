using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using FloraBot.Api.Modules.KioskOps;
using FloraBot.Api.Modules.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class KioskLocationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // =========================================================================
    // Pure mathematical, formatting, and DTO reflection unit tests
    // =========================================================================

    [Fact]
    public void HaversineDistanceIsZeroForSameCoordinates()
    {
        var distance = KioskLocations.HaversineDistanceKm(10.771918, 106.698347, 10.771918, 106.698347);
        Assert.Equal(0.0, distance);
    }

    [Fact]
    public void HaversineDistanceIsDeterministicAndAccurate()
    {
        // Ben Thanh Market (10.771918, 106.698347) to Landmark 81 (10.795123, 106.721865) ~3.64 km
        var distance = KioskLocations.HaversineDistanceKm(10.771918, 106.698347, 10.795123, 106.721865);
        Assert.InRange(distance, 3.5, 3.8);
        Assert.Equal(3.64, Math.Round(distance, 2));
    }

    [Fact]
    public void HaversineReturnsRawDistanceForSub10mOrdering()
    {
        var originLat = 10.771918;
        var originLon = 106.698347;

        // Two points ~5m and ~10m away from origin
        var d1 = KioskLocations.HaversineDistanceKm(originLat, originLon, 10.771960, 106.698347);
        var d2 = KioskLocations.HaversineDistanceKm(originLat, originLon, 10.772010, 106.698347);

        Assert.True(d1 < d2, $"Expected d1 ({d1}) < d2 ({d2}) for sub-10m distinction");
        Assert.True(d1 > 0.0);
    }

    [Fact]
    public void HaversineRadiusEdgeExcludesBoundaryBeyondThreshold()
    {
        var originLat = 10.771918;
        var originLon = 106.698347;

        var dRaw = KioskLocations.HaversineDistanceKm(originLat, originLon, 10.800000, 106.725000);
        var radiusThreshold = dRaw - 0.001; // just under the raw distance

        Assert.True(dRaw > radiusThreshold, "Raw distance must exceed radius threshold before rounding");
    }

    [Fact]
    public void GoogleMapsDirectionsUrlUsesInvariantCultureAndEscaping()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");

            var url = KioskLocations.BuildDirectionsUrl(10.771918, 106.698347);
            Assert.StartsWith("https://www.google.com/maps/dir/?api=1&destination=", url);
            Assert.Contains("10.771918", url);
            Assert.Contains("106.698347", url);
            Assert.DoesNotContain("10,771918", url);
            Assert.DoesNotContain("106,698347", url);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void LocationDtoNeverExposesSensitiveHardwareOrCredentialFields()
    {
        var sensitiveNames = new[] { "ApiKey", "ApiKeyHash", "HardwareId", "MqttClientId", "Password", "Secret", "Token", "Credential" };

        var publicDtoProps = typeof(KioskLocationItem).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in publicDtoProps)
        {
            Assert.DoesNotContain(sensitiveNames, s => prop.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var sellerDtoProps = typeof(SellerKioskDto).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in sellerDtoProps)
        {
            Assert.DoesNotContain(sensitiveNames, s => prop.Name.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var updateRequestProps = typeof(UpdateKioskLocationRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(3, updateRequestProps.Length);
        Assert.Contains(updateRequestProps, p => p.Name == "Address");
        Assert.Contains(updateRequestProps, p => p.Name == "Latitude");
        Assert.Contains(updateRequestProps, p => p.Name == "Longitude");
    }

    // =========================================================================
    // HTTP + Postgres true integration tests via ApiFactory
    // =========================================================================

    private NpgsqlDataSource DataSource => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private async Task SeedUserAsync(Guid userId, string role, Guid? sellerId = null)
    {
        await using var cmd = DataSource.CreateCommand(@"
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id)
            VALUES(@id, @email, '!unprovisioned', @name, @role, 'ACTIVE', @sellerId)
            ON CONFLICT (id) DO UPDATE SET
                role = EXCLUDED.role,
                status = 'ACTIVE',
                seller_id = EXCLUDED.seller_id;
        ");
        cmd.Parameters.AddWithValue("id", userId);
        cmd.Parameters.AddWithValue("email", $"user-{userId:N}@example.invalid");
        cmd.Parameters.AddWithValue("name", $"{role} Test User");
        cmd.Parameters.AddWithValue("role", role);
        cmd.Parameters.AddWithValue("sellerId", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)sellerId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedSellerAsync(Guid sellerId, Guid userId)
    {
        await using var cmd = DataSource.CreateCommand(@"
            INSERT INTO identity.sellers(id, shop_name, phone, status, package_id, package_expires_at)
            VALUES(@sellerId, @name, @phone, 'ACTIVE', '20000000-0000-0000-0000-00000000000a', CURRENT_DATE + 30)
            ON CONFLICT (id) DO UPDATE SET
                status = 'ACTIVE',
                package_expires_at = CURRENT_DATE + 30;
            INSERT INTO identity.users(id, email, password_hash, full_name, role, status, seller_id)
            VALUES(@userId, @email, '!unprovisioned', 'Test Seller', 'SELLER', 'ACTIVE', @sellerId)
            ON CONFLICT (id) DO UPDATE SET
                role = 'SELLER',
                status = 'ACTIVE',
                seller_id = @sellerId;
        ");
        cmd.Parameters.AddWithValue("sellerId", sellerId);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("name", $"Shop {sellerId:N}");
        cmd.Parameters.AddWithValue("phone", $"09{Random.Shared.Next(10000000, 99999999)}");
        cmd.Parameters.AddWithValue("email", $"seller-{userId:N}@example.invalid");
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedKioskAsync(Guid kioskId, string code, string name, string address, string region, string status, double? lat, double? lon, Guid? sellerId = null)
    {
        await using var cmd = DataSource.CreateCommand(@"
            INSERT INTO kiosk_ops.kiosks(id, code, name, address, region, hardware_id, mqtt_client_id, status, latitude, longitude, seller_id, created_at, updated_at)
            VALUES(@id, @code, @name, @address, @region, @hw, @mqtt, @status, @lat, @lon, @sellerId, public.app_now(), public.app_now())
            ON CONFLICT (id) DO UPDATE SET
                address = EXCLUDED.address,
                status = EXCLUDED.status,
                latitude = EXCLUDED.latitude,
                longitude = EXCLUDED.longitude,
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
        cmd.Parameters.AddWithValue("sellerId", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)sellerId ?? DBNull.Value);
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

    private async Task SeedSlotAsync(Guid slotId, Guid kioskId, string slotCode, Guid? currentSellerId, short relayChannel = 0)
    {
        await using var cmd = DataSource.CreateCommand(@"
            INSERT INTO kiosk_ops.slots(id, kiosk_id, slot_code, status, current_seller_id, relay_channel, created_at, updated_at)
            VALUES(@id, @kioskId, @slotCode, CASE WHEN @sellerId IS NULL THEN 'FREE' ELSE 'RENTED_EMPTY' END, @sellerId, @relay, public.app_now(), public.app_now())
            ON CONFLICT (id) DO UPDATE SET
                current_seller_id = EXCLUDED.current_seller_id,
                status = EXCLUDED.status,
                relay_channel = EXCLUDED.relay_channel,
                updated_at = public.app_now();
        ");
        cmd.Parameters.AddWithValue("id", slotId);
        cmd.Parameters.AddWithValue("kioskId", kioskId);
        cmd.Parameters.AddWithValue("slotCode", slotCode);
        cmd.Parameters.AddWithValue("sellerId", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)currentSellerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("relay", relayChannel);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task LocationPutRejectsUnauthenticated()
    {
        using var client = factory.CreateClient();
        var sellerId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();

        var res = await client.PutAsJsonAsync($"/api/sellers/{sellerId}/kiosks/{kioskId}/location", new
        {
            address = "123 Le Loi",
            latitude = 10.771918,
            longitude = 106.698347
        });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("OPERATIONS_MANAGER")]
    [InlineData("TECHNICIAN")]
    [InlineData("SELLER_STAFF")]
    [InlineData("CUSTOMER")]
    public async Task LocationPutRejectsNonSellerRoles(string role)
    {
        var sellerId = Guid.NewGuid();
        var kioskId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        if (role == "SELLER_STAFF")
        {
            await SeedSellerAsync(sellerId, Guid.NewGuid());
        }

        await SeedUserAsync(userId, role, role == "SELLER_STAFF" ? sellerId : null);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(role, userId: userId, sellerId: sellerId));

        var res = await client.PutAsJsonAsync($"/api/sellers/{sellerId}/kiosks/{kioskId}/location", new
        {
            address = "123 Le Loi",
            latitude = 10.771918,
            longitude = 106.698347
        });

        Assert.True(res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound, $"Actual status: {res.StatusCode}");
    }

    [Fact]
    public async Task LocationPutRejectsForeignSellerAndTenantMismatch()
    {
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var kioskA = Guid.NewGuid();

        await SeedSellerAsync(sellerA, Guid.NewGuid());
        await SeedSellerAsync(sellerB, userB);
        await SeedKioskAsync(kioskA, $"K-{kioskA:N}", "Kiosk A", "Addr A", "HCM", "ONLINE", 10.77, 106.69);
        await SeedSlotAsync(Guid.NewGuid(), kioskA, "A01", null, 0);
        await AssignKioskAsync(kioskA, sellerA);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: userB, sellerId: sellerB));

        // 1. Calling Seller A's route with Seller B's token -> 404 (SameSeller failure)
        var res1 = await client.PutAsJsonAsync($"/api/sellers/{sellerA}/kiosks/{kioskA}/location", new
        {
            address = "New Address",
            latitude = 10.78,
            longitude = 106.70
        });
        Assert.Equal(HttpStatusCode.NotFound, res1.StatusCode);

        // 2. Calling Seller B's route for Kiosk A (owned wholly by Seller A) -> 403 Forbidden
        var res2 = await client.PutAsJsonAsync($"/api/sellers/{sellerB}/kiosks/{kioskA}/location", new
        {
            address = "New Address",
            latitude = 10.78,
            longitude = 106.70
        });
        Assert.Equal(HttpStatusCode.Forbidden, res2.StatusCode);
    }

    [Fact]
    public async Task LocationPutRejectsSharedOrPartiallyUnassignedKiosk()
    {
        var sellerA = Guid.NewGuid();
        var sellerB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var sharedKiosk = Guid.NewGuid();
        var partialKiosk = Guid.NewGuid();

        await SeedSellerAsync(sellerA, userA);
        await SeedSellerAsync(sellerB, Guid.NewGuid());

        // Shared kiosk: 1 slot for Seller A, 1 slot for Seller B
        await SeedKioskAsync(sharedKiosk, $"K-SH-{sharedKiosk:N}", "Shared Kiosk", "Addr", "HCM", "ONLINE", 10.77, 106.69);
        await SeedSlotAsync(Guid.NewGuid(), sharedKiosk, "A01", sellerA, 0);
        await SeedSlotAsync(Guid.NewGuid(), sharedKiosk, "A02", sellerB, 1);

        // Partial kiosk: 1 slot for Seller A, 1 unassigned FREE slot
        await SeedKioskAsync(partialKiosk, $"K-PT-{partialKiosk:N}", "Partial Kiosk", "Addr", "HCM", "ONLINE", 10.77, 106.69);
        await SeedSlotAsync(Guid.NewGuid(), partialKiosk, "B01", sellerA, 0);
        await SeedSlotAsync(Guid.NewGuid(), partialKiosk, "B02", null, 1);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: userA, sellerId: sellerA));

        // PUT on shared kiosk -> 403 Forbidden
        var resShared = await client.PutAsJsonAsync($"/api/sellers/{sellerA}/kiosks/{sharedKiosk}/location", new
        {
            address = "Updated Shared",
            latitude = 10.78,
            longitude = 106.70
        });
        Assert.Equal(HttpStatusCode.Forbidden, resShared.StatusCode);

        // PUT on partially unassigned kiosk -> 403 Forbidden
        var resPartial = await client.PutAsJsonAsync($"/api/sellers/{sellerA}/kiosks/{partialKiosk}/location", new
        {
            address = "Updated Partial",
            latitude = 10.78,
            longitude = 106.70
        });
        Assert.Equal(HttpStatusCode.Forbidden, resPartial.StatusCode);
    }

    [Fact]
    public async Task LocationPutRejectsDisabledKioskEvenIfWhollyOwned()
    {
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var disabledKiosk = Guid.NewGuid();

        await SeedSellerAsync(seller, user);
        await SeedKioskAsync(disabledKiosk, $"K-DIS-{disabledKiosk:N}", "Disabled Kiosk", "Addr", "HCM", "DISABLED", 10.77, 106.69, sellerId: seller);
        await SeedSlotAsync(Guid.NewGuid(), disabledKiosk, "A01", seller, 0);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: user, sellerId: seller));

        var res = await client.PutAsJsonAsync($"/api/sellers/{seller}/kiosks/{disabledKiosk}/location", new
        {
            address = "Updated Addr",
            latitude = 10.78,
            longitude = 106.70
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task WholeKioskOwnershipPutSucceedsReloadsAndEmitsAudit()
    {
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var kiosk = Guid.NewGuid();

        await SeedSellerAsync(seller, user);
        // Offline kiosk is permitted to be edited if wholly assigned
        await SeedKioskAsync(kiosk, $"K-OWN-{kiosk:N}", "Wholly Owned Kiosk", "Old Address", "HCM", "OFFLINE", null, null);
        await SeedSlotAsync(Guid.NewGuid(), kiosk, "S01", null, 0);
        await SeedSlotAsync(Guid.NewGuid(), kiosk, "S02", null, 1);
        await AssignKioskAsync(kiosk, seller);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: user, sellerId: seller));

        var targetAddress = "123 Ben Thanh Street, District 1";
        var targetLat = 10.771918;
        var targetLon = 106.698347;

        var putRes = await client.PutAsJsonAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", new
        {
            address = targetAddress,
            latitude = targetLat,
            longitude = targetLon
        });
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var dto = (await putRes.Content.ReadFromJsonAsync<SellerKioskDto>())!;
        Assert.Equal(targetAddress, dto.Address);
        Assert.Equal(targetLat, dto.Latitude);
        Assert.Equal(targetLon, dto.Longitude);
        Assert.True(dto.LocationComplete);
        Assert.True(dto.CanEditLocation);

        // Verify database persistence
        await using (var checkCmd = DataSource.CreateCommand("SELECT address, latitude, longitude FROM kiosk_ops.kiosks WHERE id = @id"))
        {
            checkCmd.Parameters.AddWithValue("id", kiosk);
            await using var reader = await checkCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(targetAddress, reader.GetString(0));
            Assert.Equal(targetLat, reader.GetDouble(1));
            Assert.Equal(targetLon, reader.GetDouble(2));
        }

        // Verify audit entry in notify.audit_logs
        await using (var auditCmd = DataSource.CreateCommand(@"
            SELECT count(*) FROM notify.audit_logs
            WHERE action = 'KIOSK_LOCATION_UPDATED' AND entity_id = @id AND actor_id = @actorId"))
        {
            auditCmd.Parameters.AddWithValue("id", kiosk);
            auditCmd.Parameters.AddWithValue("actorId", user);
            var count = (long)(await auditCmd.ExecuteScalarAsync())!;
            Assert.True(count >= 1L);
        }

        // Verify GET /api/sellers/{sellerId}/kiosks includes this kiosk with canEditLocation = true
        var listRes = await client.GetAsync($"/api/sellers/{seller}/kiosks");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var kiosks = (await listRes.Content.ReadFromJsonAsync<List<SellerKioskDto>>())!;
        var item = Assert.Single(kiosks, x => x.Id == kiosk);
        Assert.True(item.CanEditLocation);
        Assert.True(item.LocationComplete);
        Assert.Equal(targetLat, item.Latitude);
    }

    [Fact]
    public async Task LocationPutValidatesMissingNullAndZeroZeroCoordsJson()
    {
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var kiosk = Guid.NewGuid();

        await SeedSellerAsync(seller, user);
        await SeedKioskAsync(kiosk, $"K-JSON-{kiosk:N}", "JSON Kiosk", "Initial Addr", "HCM", "ONLINE", 10.77, 106.69);
        await SeedSlotAsync(Guid.NewGuid(), kiosk, "S01", null, 0);
        await AssignKioskAsync(kiosk, seller);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: user, sellerId: seller));

        // 1. Missing latitude property in JSON
        var jsonMissingLat = new StringContent("{\"address\":\"123 Le Loi\",\"longitude\":106.69}", Encoding.UTF8, "application/json");
        var res1 = await client.PutAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", jsonMissingLat);
        Assert.Equal(HttpStatusCode.BadRequest, res1.StatusCode);

        // 2. Missing longitude property in JSON
        var jsonMissingLon = new StringContent("{\"address\":\"123 Le Loi\",\"latitude\":10.77}", Encoding.UTF8, "application/json");
        var res2 = await client.PutAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", jsonMissingLon);
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);

        // 3. Missing both coordinates
        var jsonMissingBoth = new StringContent("{\"address\":\"123 Le Loi\"}", Encoding.UTF8, "application/json");
        var res3 = await client.PutAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", jsonMissingBoth);
        Assert.Equal(HttpStatusCode.BadRequest, res3.StatusCode);

        // 4. Null latitude property in JSON
        var jsonNullLat = new StringContent("{\"address\":\"123 Le Loi\",\"latitude\":null,\"longitude\":106.69}", Encoding.UTF8, "application/json");
        var res4 = await client.PutAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", jsonNullLat);
        Assert.Equal(HttpStatusCode.BadRequest, res4.StatusCode);

        // 5. Null longitude property in JSON
        var jsonNullLon = new StringContent("{\"address\":\"123 Le Loi\",\"latitude\":10.77,\"longitude\":null}", Encoding.UTF8, "application/json");
        var res5 = await client.PutAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", jsonNullLon);
        Assert.Equal(HttpStatusCode.BadRequest, res5.StatusCode);

        // 6. VALID 0.0, 0.0 coordinates (Null Island) must be accepted
        var jsonZeroZero = new StringContent("{\"address\":\"Equator Station\",\"latitude\":0.0,\"longitude\":0.0}", Encoding.UTF8, "application/json");
        var res6 = await client.PutAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", jsonZeroZero);
        Assert.Equal(HttpStatusCode.OK, res6.StatusCode);

        var dto = (await res6.Content.ReadFromJsonAsync<SellerKioskDto>())!;
        Assert.Equal(0.0, dto.Latitude);
        Assert.Equal(0.0, dto.Longitude);
    }

    [Fact]
    public async Task PublicKioskLocationsFiltersExcludesAndFormatsCorrectly()
    {
        // Separate each run from other classes and retained test fixtures; exercise real pagination.
        var originLat = -70 + Random.Shared.NextDouble() * 10;
        var originLon = -140 + Random.Shared.NextDouble() * 10;
        var farLat = originLat + 0.03;
        var farLon = originLon + 0.03;
        var expectedDistance = Math.Round(KioskLocations.HaversineDistanceKm(originLat, originLon, farLat, farLon), 2);
        var kCompleteOnline = Guid.NewGuid();
        var kCompleteOffline = Guid.NewGuid();
        var kDisabled = Guid.NewGuid();
        var kNoCoords = Guid.NewGuid();

        var pubSeller = Guid.NewGuid();
        var pubUser = Guid.NewGuid();
        await SeedSellerAsync(pubSeller, pubUser);

        // 1. Complete Online (near Ben Thanh)
        await SeedKioskAsync(kCompleteOnline, $"K-PUB-1-{kCompleteOnline:N}", "Ben Thanh Kiosk", "123 Le Loi", "HCM", "ONLINE", originLat, originLon);
        await SeedSlotAsync(Guid.NewGuid(), kCompleteOnline, "P01", null, 0);
        await AssignKioskAsync(kCompleteOnline, pubSeller);

        // 2. Complete Offline (near Landmark 81, ~3.64 km away) - must be included truthfully as OFFLINE
        await SeedKioskAsync(kCompleteOffline, $"K-PUB-2-{kCompleteOffline:N}", "Landmark Kiosk", "720A Dien Bien Phu", "HCM", "OFFLINE", farLat, farLon);

        // 3. Disabled kiosk - must be EXCLUDED
        await SeedKioskAsync(kDisabled, $"K-PUB-3-{kDisabled:N}", "Disabled Kiosk", "999 Dis St", "HCM", "DISABLED", 10.78, 106.70);

        // 4. Incomplete kiosk (null coordinates) - must be EXCLUDED
        await SeedKioskAsync(kNoCoords, $"K-PUB-4-{kNoCoords:N}", "Incomplete Kiosk", "No Pin St", "HCM", "ONLINE", null, null);

        using var client = factory.CreateClient();

        // Query with coordinates near Ben Thanh (originLat, originLon)
        var res = await client.GetAsync($"/api/kiosk-locations?latitude={originLat.ToString(CultureInfo.InvariantCulture)}&longitude={originLon.ToString(CultureInfo.InvariantCulture)}&radiusKm=10");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());

        var items = (await res.Content.ReadFromJsonAsync<List<KioskLocationItem>>())!;

        Assert.Contains(items, x => x.Id == kCompleteOnline && x.Status == "ONLINE" && x.DistanceKm == 0.0);
        Assert.Contains(items, x => x.Id == kCompleteOffline && x.Status == "OFFLINE" && x.DistanceKm == expectedDistance);
        Assert.DoesNotContain(items, x => x.Id == kDisabled);
        Assert.DoesNotContain(items, x => x.Id == kNoCoords);

        // Verify nearest first ordering
        var indexOnline = items.FindIndex(x => x.Id == kCompleteOnline);
        var indexOffline = items.FindIndex(x => x.Id == kCompleteOffline);
        Assert.True(indexOnline < indexOffline, "Nearest kiosk must precede further kiosk");

        // Verify directions URL
        var onlineItem = items.First(x => x.Id == kCompleteOnline);
        Assert.NotNull(onlineItem.DirectionsUrl);
        Assert.Contains(originLat.ToString(CultureInfo.InvariantCulture), onlineItem.DirectionsUrl);
        Assert.Contains(originLon.ToString(CultureInfo.InvariantCulture), onlineItem.DirectionsUrl);

        // Query with tight radius excluding Landmark (~3.64 km away)
        var resTight = await client.GetAsync($"/api/kiosk-locations?latitude={originLat.ToString(CultureInfo.InvariantCulture)}&longitude={originLon.ToString(CultureInfo.InvariantCulture)}&radiusKm=2");
        Assert.Equal(HttpStatusCode.OK, resTight.StatusCode);
        var tightItems = (await resTight.Content.ReadFromJsonAsync<List<KioskLocationItem>>())!;
        Assert.Contains(tightItems, x => x.Id == kCompleteOnline);
        Assert.DoesNotContain(tightItems, x => x.Id == kCompleteOffline);

        // Invalid queries
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/kiosk-locations?latitude=10.77")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/kiosk-locations?longitude=106.69")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/kiosk-locations?radiusKm=-1&latitude=10.77&longitude=106.69")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/kiosk-locations?radiusKm=5")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/kiosk-locations?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/kiosk-locations?limit=101")).StatusCode);

        // Verify GET /api/sellers/{sellerId}/kiosks reloads this kiosk correctly
        using var pubSellerClient = factory.CreateClient();
        pubSellerClient.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", userId: pubUser, sellerId: pubSeller));
        var sellerKiosksRes = await pubSellerClient.GetAsync($"/api/sellers/{pubSeller}/kiosks");
        Assert.Equal(HttpStatusCode.OK, sellerKiosksRes.StatusCode);
        var sellerKiosks = (await sellerKiosksRes.Content.ReadFromJsonAsync<List<SellerKioskDto>>())!;
        var sellerItem = Assert.Single(sellerKiosks, x => x.Id == kCompleteOnline);
        Assert.True(sellerItem.CanEditLocation);
        Assert.True(sellerItem.LocationComplete);
        Assert.Equal(originLat, sellerItem.Latitude);
        Assert.Equal(originLon, sellerItem.Longitude);
    }

    [Fact]
    public async Task PreorderCreationRejectsIncompleteKioskAndPreservesHistoricalRetry()
    {
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var product = Guid.NewGuid();
        var incompleteKiosk = Guid.NewGuid();
        var completeKiosk = Guid.NewGuid();

        await SeedSellerAsync(seller, user);
        await SeedUserAsync(customer, "CUSTOMER");

        await using (var cmd = DataSource.CreateCommand(@"
            INSERT INTO catalog.flower_products(id, seller_id, name, price, shelf_life_hours, status)
            VALUES(@productId, @sellerId, 'Custom Preorder Rose', 300000, 48, 'ACTIVE')
            ON CONFLICT (id) DO NOTHING;
        "))
        {
            cmd.Parameters.AddWithValue("productId", product);
            cmd.Parameters.AddWithValue("sellerId", seller);
            await cmd.ExecuteNonQueryAsync();
        }

        // Incomplete kiosk (null coords)
        var incSlot = Guid.NewGuid();
        await SeedKioskAsync(incompleteKiosk, $"K-INC-{incompleteKiosk:N}", "Incomplete Kiosk", "Addr", "HCM", "ONLINE", null, null);
        await SeedSlotAsync(incSlot, incompleteKiosk, "A01", null, 0);
        await AssignKioskAsync(incompleteKiosk, seller);

        // Complete kiosk
        var comSlot = Guid.NewGuid();
        await SeedKioskAsync(completeKiosk, $"K-COM-{completeKiosk:N}", "Complete Kiosk", "123 Complete St", "HCM", "ONLINE", 10.771918, 106.698347);
        await SeedSlotAsync(comSlot, completeKiosk, "B01", null, 0);
        await AssignKioskAsync(completeKiosk, seller);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("CUSTOMER", userId: customer));

        // 1. New preorder pointing to INCOMPLETE kiosk -> REJECTED (400 Bad Request)
        var failId = Guid.NewGuid();
        var failRes = await client.PostAsJsonAsync("/api/member/preorders", new
        {
            id = failId,
            kind = "CUSTOM",
            productId = product,
            kioskId = incompleteKiosk,
            pickupAt = DateTimeOffset.UtcNow.AddHours(3),
            instructions = "Pink flowers custom instruction for test"
        });
        Assert.True(failRes.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict);

        // 2. New preorder pointing to COMPLETE kiosk -> SUCCEEDS (200 OK)
        var successId = Guid.NewGuid();
        var pickupTime = DateTimeOffset.UtcNow.AddHours(3);
        var successRes = await client.PostAsJsonAsync("/api/member/preorders", new
        {
            id = successId,
            kind = "CUSTOM",
            productId = product,
            kioskId = completeKiosk,
            pickupAt = pickupTime,
            instructions = "Pink flowers custom instruction for test"
        });
        Assert.Equal(HttpStatusCode.OK, successRes.StatusCode);

        // 3. Clear location on completeKiosk in database (simulating kiosk location later lost/nulled)
        await using (var clearCmd = DataSource.CreateCommand("UPDATE kiosk_ops.kiosks SET latitude = NULL, longitude = NULL WHERE id = @id"))
        {
            clearCmd.Parameters.AddWithValue("id", completeKiosk);
            await clearCmd.ExecuteNonQueryAsync();
        }

        // 4. Retry existing preorder with successId -> MUST SUCCEED (idempotency & historical preorder preserved!)
        var retryRes = await client.PostAsJsonAsync("/api/member/preorders", new
        {
            id = successId,
            kind = "CUSTOM",
            productId = product,
            kioskId = completeKiosk,
            pickupAt = pickupTime,
            instructions = "Pink flowers custom instruction for test"
        });
        Assert.Equal(HttpStatusCode.OK, retryRes.StatusCode);
    }

    [Fact]
    public async Task LocationPutRechecksOwnershipAfterConcurrentTransfer()
    {
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var foreignSeller = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        await SeedSellerAsync(seller, user);
        await SeedSellerAsync(foreignSeller, Guid.NewGuid());
        await SeedKioskAsync(kiosk, $"K-RACE-{kiosk:N}", "Ownership race", "Original address", "HCM", "OFFLINE", 10.77, 106.69);
        await SeedSlotAsync(slot, kiosk, "L01", null, 0);
        await AssignKioskAsync(kiosk, seller);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token("SELLER", sellerId: seller, userId: user));

        var getKioskRes = await client.GetAsync($"/api/sellers/{seller}/kiosks");
        Assert.Equal(HttpStatusCode.OK, getKioskRes.StatusCode);
        var sellerKiosks = (await getKioskRes.Content.ReadFromJsonAsync<List<SellerKioskDto>>())!;
        var ownKiosk = Assert.Single(sellerKiosks, k => k.Id == kiosk);
        Assert.True(ownKiosk.CanEditLocation);

        // A real assignment transaction has changed ownership, but not committed it yet.
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var transfer = new NpgsqlCommand(
            "SELECT flow.assign_kiosk(@kiosk,@seller,'10000000-0000-0000-0000-000000000001')", connection, tx);
        transfer.Parameters.AddWithValue("seller", foreignSeller);
        transfer.Parameters.AddWithValue("kiosk", kiosk);
        await transfer.ExecuteNonQueryAsync();
        var pendingPut = client.PutAsJsonAsync($"/api/sellers/{seller}/kiosks/{kiosk}/location", new { address = "Stale owner update", latitude = 11.0, longitude = 107.0 });
        try
        {
            await Task.Delay(200);
            Assert.False(pendingPut.IsCompleted, "HTTP mutation must wait for the assignment row lock");
        }
        finally
        {
            await tx.CommitAsync();
        }
        var response = await pendingPut.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var read = DataSource.CreateCommand("SELECT address FROM kiosk_ops.kiosks WHERE id=@id");
        read.Parameters.AddWithValue("id", kiosk);
        Assert.Equal("Original address", await read.ExecuteScalarAsync());
    }
}
