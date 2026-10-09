using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ProductCatalogTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("", 100000, 10, 10, 10, "tag1")] // Empty name
    [InlineData("   ", 100000, 10, 10, 10, "tag1")] // Whitespace name
    [InlineData("Valid", 0, 10, 10, 10, "tag1")] // Zero price
    [InlineData("Valid", -500, 10, 10, 10, "tag1")] // Negative price
    [InlineData("Valid", 100000, 0, 10, 10, "tag1")] // Zero length
    [InlineData("Valid", 100000, 10, -5, 10, "tag1")] // Negative width
    [InlineData("Valid", 100000, 10, 10, 1001, "tag1")] // Height > 1000
    [InlineData("Valid", 100000, 0.004, 10, 10, "tag1")] // Precision edge: 0.004 cm (< 0.01 or scale > 2)
    public async Task RequestValidationRejectsInvalidMetadataWithBadRequest(
        string name, long price, decimal length, decimal width, decimal height, string tag)
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Validation Shop','0901234580','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Validation Seller','SELLER',@seller,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("user", user);
        await setup.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));

            var invalidBody = new CreateProductRequest(
                Guid.NewGuid(), name, "Desc", price, [tag], length, width, height);

            var response = await client.PostAsJsonAsync($"/api/sellers/{seller}/products", invalidBody);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM identity.users WHERE id=@user;
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("user", user);
            cleanup.Parameters.AddWithValue("seller", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RequestValidationRejectsRawJsonNullTagsAndTagsContainingNull()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Raw JSON Shop','0901234581','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Raw JSON Seller','SELLER',@seller,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("user", user);
        await setup.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));

            // 1. Raw JSON: tags is null
            var jsonNullTags = $$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "name": "Hoa Vải",
                  "description": "Mô tả",
                  "price": 100000,
                  "tags": null,
                  "lengthCm": 10.0,
                  "widthCm": 10.0,
                  "heightCm": 10.0
                }
                """;
            var content1 = new StringContent(jsonNullTags, Encoding.UTF8, "application/json");
            var res1 = await client.PostAsync($"/api/sellers/{seller}/products", content1);
            Assert.Equal(HttpStatusCode.BadRequest, res1.StatusCode);

            // 2. Raw JSON: tags contains null element
            var jsonTagWithNull = $$"""
                {
                  "id": "{{Guid.NewGuid()}}",
                  "name": "Hoa Vải",
                  "description": "Mô tả",
                  "price": 100000,
                  "tags": ["valid-tag", null],
                  "lengthCm": 10.0,
                  "widthCm": 10.0,
                  "heightCm": 10.0
                }
                """;
            var content2 = new StringContent(jsonTagWithNull, Encoding.UTF8, "application/json");
            var res2 = await client.PostAsync($"/api/sellers/{seller}/products", content2);
            Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM identity.users WHERE id=@user;
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("user", user);
            cleanup.Parameters.AddWithValue("seller", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task NullDescriptionRoundtripsSuccessfullyOnBothPostAndPut()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var productId = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Null Desc Shop','0901234570','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Null Desc Seller','SELLER',@seller,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("user", user);
        await setup.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));

            // POST with null description
            var postBody = new CreateProductRequest(
                productId, "Hoa Không Mô Tả", null, 200000, ["TAG1"], 15, 15, 20);
            var postRes = await client.PostAsJsonAsync($"/api/sellers/{seller}/products", postBody);
            Assert.Equal(HttpStatusCode.Created, postRes.StatusCode);

            // GET verifies description is null
            var getRes1 = await client.GetAsync($"/api/sellers/{seller}/products");
            var items1 = await getRes1.Content.ReadFromJsonAsync<List<ProductResponse>>();
            var created = Assert.Single(items1!, p => p.Id == productId);
            Assert.Null(created.Description);

            // PUT updating while keeping null description
            var putBody = new UpdateProductRequest(
                "Hoa Đã Đổi Tên", null, 220000, ["TAG1", "TAG2"], 15, 15, 20);
            var putRes = await client.PutAsJsonAsync($"/api/sellers/{seller}/products/{productId}", putBody);
            Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

            // GET verifies update with null description
            var getRes2 = await client.GetAsync($"/api/sellers/{seller}/products");
            var items2 = await getRes2.Content.ReadFromJsonAsync<List<ProductResponse>>();
            var updated = Assert.Single(items2!, p => p.Id == productId);
            Assert.Equal("Hoa Đã Đổi Tên", updated.Name);
            Assert.Null(updated.Description);
            Assert.Equal(220000L, updated.Price);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM catalog.flower_products WHERE id=@product;
                DELETE FROM identity.users WHERE id=@user;
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("product", productId);
            cleanup.Parameters.AddWithValue("user", user);
            cleanup.Parameters.AddWithValue("seller", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task DirectSqlValidationsRejectZeroUuidAndBypassedInvalidInputs()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Direct SQL Shop','0901234579','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Direct SQL Seller','SELLER',@seller,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("user", user);
        await setup.ExecuteNonQueryAsync();

        try
        {
            // 1. Zero UUID for p_id must throw 22023
            await using var cmdZeroId = data.CreateCommand("""
                SELECT flow.create_artificial_product(
                    '00000000-0000-0000-0000-000000000000'::uuid,
                    @seller, @actor, 'Test', NULL, 100000, '{"TAG"}'::text[], 10, 10, 10)
                """);
            cmdZeroId.Parameters.AddWithValue("seller", seller);
            cmdZeroId.Parameters.AddWithValue("actor", user);
            var exZero = await Assert.ThrowsAsync<PostgresException>(() => cmdZeroId.ExecuteScalarAsync());
            Assert.Equal("22023", exZero.SqlState);

            // 2. Direct SQL null tag in array must throw 22023
            await using var cmdNullTag = data.CreateCommand("""
                SELECT flow.create_artificial_product(
                    @id, @seller, @actor, 'Test', NULL, 100000, ARRAY['tag1', NULL]::text[], 10, 10, 10)
                """);
            cmdNullTag.Parameters.AddWithValue("id", Guid.NewGuid());
            cmdNullTag.Parameters.AddWithValue("seller", seller);
            cmdNullTag.Parameters.AddWithValue("actor", user);
            var exNullTag = await Assert.ThrowsAsync<PostgresException>(() => cmdNullTag.ExecuteScalarAsync());
            Assert.Equal("22023", exNullTag.SqlState);

            // 3. Direct SQL precision edge 0.004 cm must throw 22023
            await using var cmdBadDim = data.CreateCommand("""
                SELECT flow.create_artificial_product(
                    @id, @seller, @actor, 'Test', NULL, 100000, '{"TAG"}'::text[], 0.004, 10, 10)
                """);
            cmdBadDim.Parameters.AddWithValue("id", Guid.NewGuid());
            cmdBadDim.Parameters.AddWithValue("seller", seller);
            cmdBadDim.Parameters.AddWithValue("actor", user);
            var exBadDim = await Assert.ThrowsAsync<PostgresException>(() => cmdBadDim.ExecuteScalarAsync());
            Assert.Equal("22023", exBadDim.SqlState);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM identity.users WHERE id=@user;
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("user", user);
            cleanup.Parameters.AddWithValue("seller", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task TenantIsolationAndRoleAuthorizationEnforcesSellerOwnership()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var ownSeller = Guid.NewGuid();
        var foreignSeller = Guid.NewGuid();
        var ownUser = Guid.NewGuid();
        var adminUser = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Tenant Shop','0901234582','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Tenant Seller','SELLER',@seller,'ACTIVE'),
                  (@admin,@admin::text||'@example.invalid','!unprovisioned','Tenant Admin','ADMIN',NULL,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("seller", ownSeller);
        setup.Parameters.AddWithValue("user", ownUser);
        setup.Parameters.AddWithValue("admin", adminUser);
        await setup.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();

            // Foreign seller write returns 404 (SameSeller tenant protection)
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: ownUser, sellerId: ownSeller));
            var body = new CreateProductRequest(Guid.NewGuid(), "Flower", "Desc", 50000, ["rose"], 10, 10, 10);
            var foreignResponse = await client.PostAsJsonAsync($"/api/sellers/{foreignSeller}/products", body);
            Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);

            // Admin cannot use owner product write API (policy is Seller + SameSeller)
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: adminUser, role: "ADMIN"));
            var adminResponse = await client.PostAsJsonAsync($"/api/sellers/{ownSeller}/products", body);
            Assert.Equal(HttpStatusCode.Forbidden, adminResponse.StatusCode);

            // Anonymous returns 401
            client.DefaultRequestHeaders.Authorization = null;
            var anonResponse = await client.PostAsJsonAsync($"/api/sellers/{ownSeller}/products", body);
            Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM identity.users WHERE id IN (@user, @admin);
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("user", ownUser);
            cleanup.Parameters.AddWithValue("admin", adminUser);
            cleanup.Parameters.AddWithValue("seller", ownSeller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CreateArtificialProductPersistsMetadataAndRejectsDuplicateIdWithConflict()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var productId = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Catalog Shop','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Catalog Seller','SELLER',@seller,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("user", user);
        await setup.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));

            var createBody = new CreateProductRequest(
                productId, "Hoa Hồng Giả", "Hoa lụa cao cấp", 250000, ["HONG", "LUA"], 25.5m, 15.0m, 30.0m);

            var createResponse = await client.PostAsJsonAsync($"/api/sellers/{seller}/products", createBody);
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

            var createdResult = await createResponse.Content.ReadFromJsonAsync<ProductIdResponse>();
            Assert.NotNull(createdResult);
            Assert.Equal(productId, createdResult.Id);

            // Duplicate ID must return 409 Conflict without creating a duplicate
            var duplicateResponse = await client.PostAsJsonAsync($"/api/sellers/{seller}/products", createBody);
            Assert.Equal(HttpStatusCode.Conflict, duplicateResponse.StatusCode);

            // Verify metadata via GET /api/sellers/{sellerId}/products
            var getResponse = await client.GetAsync($"/api/sellers/{seller}/products");
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

            var products = await getResponse.Content.ReadFromJsonAsync<List<ProductResponse>>();
            Assert.NotNull(products);
            var item = Assert.Single(products, p => p.Id == productId);
            Assert.Equal("Hoa Hồng Giả", item.Name);
            Assert.Equal("Hoa lụa cao cấp", item.Description);
            Assert.Equal(250000L, item.Price);
            Assert.Equal("DRAFT", item.Status);
            Assert.Equal("ARTIFICIAL", item.InventoryKind);
            Assert.Null(item.ShelfLifeHours); // Artificial flowers have null shelfLifeHours on UI/DTO
            Assert.Equal(25.5m, item.LengthCm);
            Assert.Equal(15.0m, item.WidthCm);
            Assert.Equal(30.0m, item.HeightCm);

            // Audit row persisted
            await using var auditCheck = data.CreateCommand("""
                SELECT count(*) FROM notify.audit_logs
                WHERE actor_id=@actor AND entity_id=@product AND action='PRODUCT_CREATED'
                """);
            auditCheck.Parameters.AddWithValue("actor", user);
            auditCheck.Parameters.AddWithValue("product", productId);
            Assert.Equal(1L, await auditCheck.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM catalog.flower_products WHERE id=@product;
                DELETE FROM identity.users WHERE id=@user;
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("product", productId);
            cleanup.Parameters.AddWithValue("user", user);
            cleanup.Parameters.AddWithValue("seller", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task UpdateArtificialProductModifiesMetadataAndBlocksDimensionChangeOnLiveBouquet()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var bouquetId = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Update Shop','0901234568','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Update Seller','SELLER',@seller,'ACTIVE');
            INSERT INTO catalog.flower_products(id,seller_id,name,description,price,tags,shelf_life_hours,status,inventory_kind,length_cm,width_cm,height_cm)
            VALUES(@product,@seller,'Hoa Mau Don','Mo ta',300000,'{"DON"}',48,'ACTIVE','ARTIFICIAL',20,20,30);
            """);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("user", user);
        setup.Parameters.AddWithValue("product", productId);
        await setup.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));

            // 1. Update metadata without dimension change succeeds
            var updateMetadata = new UpdateProductRequest(
                "Hoa Mẫu Đơn Đỏ", "Mô tả mới", 320000, ["DON", "DO"], 20, 20, 30);
            var updateRes = await client.PutAsJsonAsync($"/api/sellers/{seller}/products/{productId}", updateMetadata);
            Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);

            // 2. Add a live bouquet (STOCKED)
            await using var addBouquet = data.CreateCommand("""
                INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,sellable_until,stocked_at)
                VALUES(@bouquet,@product,@seller,'QR-TEST-ARTIFICIAL-1',320000,'STOCKED',CURRENT_TIMESTAMP+INTERVAL '1 day',CURRENT_TIMESTAMP);
                """);
            addBouquet.Parameters.AddWithValue("bouquet", bouquetId);
            addBouquet.Parameters.AddWithValue("product", productId);
            addBouquet.Parameters.AddWithValue("seller", seller);
            await addBouquet.ExecuteNonQueryAsync();

            // Check that trigger set sellable_until to 'infinity' for ARTIFICIAL bouquet
            await using var checkTrigger = data.CreateCommand("""
                SELECT sellable_until FROM kiosk_ops.bouquets WHERE id=@bouquet
                """);
            checkTrigger.Parameters.AddWithValue("bouquet", bouquetId);
            var sellableUntil = (DateTime)(await checkTrigger.ExecuteScalarAsync())!;
            Assert.Equal(DateTime.MaxValue, sellableUntil);

            // 3. Attempting dimension change with live bouquet must return 409 Conflict
            var dimensionChange = new UpdateProductRequest(
                "Hoa Mẫu Đơn Đỏ", "Mô tả mới", 320000, ["DON", "DO"], 25, 20, 30); // length changed from 20 to 25
            var conflictRes = await client.PutAsJsonAsync($"/api/sellers/{seller}/products/{productId}", dimensionChange);
            Assert.Equal(HttpStatusCode.Conflict, conflictRes.StatusCode);

            // 4. Update non-dimension metadata with live bouquet still succeeds
            var nameOnlyUpdate = new UpdateProductRequest(
                "Hoa Mẫu Đơn Trắng", "Mô tả mới", 350000, ["DON", "TRANG"], 20, 20, 30);
            var successRes = await client.PutAsJsonAsync($"/api/sellers/{seller}/products/{productId}", nameOnlyUpdate);
            Assert.Equal(HttpStatusCode.OK, successRes.StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM kiosk_ops.bouquets WHERE id=@bouquet;
                DELETE FROM catalog.flower_products WHERE id=@product;
                DELETE FROM identity.users WHERE id=@user;
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("bouquet", bouquetId);
            cleanup.Parameters.AddWithValue("product", productId);
            cleanup.Parameters.AddWithValue("user", user);
            cleanup.Parameters.AddWithValue("seller", seller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task AuthorizationAndTenantHidingEnforcesInactiveSellersAndForeignResources()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var sellerA = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var productA = Guid.NewGuid();

        var sellerB = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var productB = Guid.NewGuid();

        var inactiveSeller = Guid.NewGuid();
        var inactiveUser = Guid.NewGuid();

        var expiredSeller = Guid.NewGuid();
        var expiredUser = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            -- Active Seller A
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@sellerA,'Shop A','0901234571','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@userA,@userA::text||'@example.invalid','!unprovisioned','Seller A','SELLER',@sellerA,'ACTIVE');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,inventory_kind,length_cm,width_cm,height_cm)
            VALUES(@productA,@sellerA,'Product A',100000,48,'ACTIVE','ARTIFICIAL',10,10,10);

            -- Active Seller B
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@sellerB,'Shop B','0901234572','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@userB,@userB::text||'@example.invalid','!unprovisioned','Seller B','SELLER',@sellerB,'ACTIVE');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,inventory_kind,length_cm,width_cm,height_cm)
            VALUES(@productB,@sellerB,'Product B',100000,48,'ACTIVE','ARTIFICIAL',10,10,10);

            -- Inactive Seller
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@inactiveSeller,'Inactive Shop','0901234573','SUSPENDED','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@inactiveUser,@inactiveUser::text||'@example.invalid','!unprovisioned','Inactive User','SELLER',@inactiveSeller,'ACTIVE');

            -- Expired subscription package seller
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@expiredSeller,'Expired Shop','0901234574','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE-1);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@expiredUser,@expiredUser::text||'@example.invalid','!unprovisioned','Expired User','SELLER',@expiredSeller,'ACTIVE');
            """);
        setup.Parameters.AddWithValue("sellerA", sellerA); setup.Parameters.AddWithValue("userA", userA); setup.Parameters.AddWithValue("productA", productA);
        setup.Parameters.AddWithValue("sellerB", sellerB); setup.Parameters.AddWithValue("userB", userB); setup.Parameters.AddWithValue("productB", productB);
        setup.Parameters.AddWithValue("inactiveSeller", inactiveSeller); setup.Parameters.AddWithValue("inactiveUser", inactiveUser);
        setup.Parameters.AddWithValue("expiredSeller", expiredSeller); setup.Parameters.AddWithValue("expiredUser", expiredUser);
        await setup.ExecuteNonQueryAsync();

        try
        {
            using var client = factory.CreateClient();

            // 1. PUT on foreign seller route -> 404
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: userA, sellerId: sellerA));
            var updateBody = new UpdateProductRequest("New Name", null, 150000, ["TAG"], 10, 10, 10);
            var putForeignSeller = await client.PutAsJsonAsync($"/api/sellers/{sellerB}/products/{productB}", updateBody);
            Assert.Equal(HttpStatusCode.NotFound, putForeignSeller.StatusCode);

            // 2. PUT on missing product ID for own seller -> 404
            var putMissingProduct = await client.PutAsJsonAsync($"/api/sellers/{sellerA}/products/{Guid.NewGuid()}", updateBody);
            Assert.Equal(HttpStatusCode.NotFound, putMissingProduct.StatusCode);

            // 3. PUT with product belonging to another seller on own seller route -> 404
            var putForeignProduct = await client.PutAsJsonAsync($"/api/sellers/{sellerA}/products/{productB}", updateBody);
            Assert.Equal(HttpStatusCode.NotFound, putForeignProduct.StatusCode);

            // 4. Inactive seller attempting POST -> 403
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: inactiveUser, sellerId: inactiveSeller));
            var postInactive = await client.PostAsJsonAsync($"/api/sellers/{inactiveSeller}/products",
                new CreateProductRequest(Guid.NewGuid(), "Name", null, 100000, ["TAG"], 10, 10, 10));
            Assert.Equal(HttpStatusCode.Forbidden, postInactive.StatusCode);

            // 5. Expired subscription seller attempting POST -> 403
            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: expiredUser, sellerId: expiredSeller));
            var postExpired = await client.PostAsJsonAsync($"/api/sellers/{expiredSeller}/products",
                new CreateProductRequest(Guid.NewGuid(), "Name", null, 100000, ["TAG"], 10, 10, 10));
            Assert.Equal(HttpStatusCode.Forbidden, postExpired.StatusCode);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM catalog.flower_products WHERE id IN (@productA, @productB);
                DELETE FROM identity.users WHERE id IN (@userA, @userB, @inactiveUser, @expiredUser);
                DELETE FROM identity.sellers WHERE id IN (@sellerA, @sellerB, @inactiveSeller, @expiredSeller);
                """);
            cleanup.Parameters.AddWithValue("productA", productA); cleanup.Parameters.AddWithValue("productB", productB);
            cleanup.Parameters.AddWithValue("userA", userA); cleanup.Parameters.AddWithValue("userB", userB);
            cleanup.Parameters.AddWithValue("inactiveUser", inactiveUser); cleanup.Parameters.AddWithValue("expiredUser", expiredUser);
            cleanup.Parameters.AddWithValue("sellerA", sellerA); cleanup.Parameters.AddWithValue("sellerB", sellerB);
            cleanup.Parameters.AddWithValue("inactiveSeller", inactiveSeller); cleanup.Parameters.AddWithValue("expiredSeller", expiredSeller);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task OriginalStockFlowSetsNonExpiringSellableUntilAndIgnoresExpiryRoutine()
    {
        using var scope = factory.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        var seller = Guid.NewGuid();
        var user = Guid.NewGuid();
        var product = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var batch = Guid.NewGuid();

        await using var setup = data.CreateCommand("""
            -- Kiosk and active slot
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Kiosk Test','123 Test St','Q1',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,status,current_seller_id,relay_channel)
            VALUES(@slot,@kiosk,'S01','RENTED_EMPTY',@seller,1);

            -- Active seller with valid subscription and slot lease
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Stock Test Shop','0901234575','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@user,@user::text||'@example.invalid','!unprovisioned','Stock Test Seller','SELLER',@seller,'ACTIVE');
            INSERT INTO kiosk_ops.slot_assignments(id,slot_id,seller_id,assigned_by,status)
            VALUES(uuid_v7(),@slot,@seller,@user,'ACTIVE');

            -- Create and activate ARTIFICIAL flower product
            INSERT INTO catalog.flower_products(id,seller_id,name,description,price,tags,shelf_life_hours,status,inventory_kind,length_cm,width_cm,height_cm)
            VALUES(@product,@seller,'Hoa Lan Giả','Mo ta',280000,'{"LAN"}',48,'ACTIVE','ARTIFICIAL',20,20,30);
            """);
        setup.Parameters.AddWithValue("kiosk", kiosk);
        setup.Parameters.AddWithValue("slot", slot);
        setup.Parameters.AddWithValue("seller", seller);
        setup.Parameters.AddWithValue("user", user);
        setup.Parameters.AddWithValue("product", product);
        await setup.ExecuteNonQueryAsync();

        // Execute ORIGINAL flow.stock_bouquet signature:
        // stock_bouquet(p_batch uuid, p_product uuid, p_slot uuid, p_staff uuid, p_qr text, p_now timestamptz DEFAULT NULL)
        var qrCode = $"QR-ARTIFICIAL-{Guid.NewGuid()}";
        await using var stockCmd = data.CreateCommand("""
            SELECT flow.stock_bouquet(@batch, @product, @slot, @actor, @qr)
            """);
        stockCmd.Parameters.AddWithValue("batch", batch);
        stockCmd.Parameters.AddWithValue("product", product);
        stockCmd.Parameters.AddWithValue("slot", slot);
        stockCmd.Parameters.AddWithValue("actor", user);
        stockCmd.Parameters.AddWithValue("qr", qrCode);
        var stockedBouquetId = (Guid)(await stockCmd.ExecuteScalarAsync())!;

        // 1. Verify that the trigger set sellable_until to 'infinity' (DateTime.MaxValue in .NET)
        await using var verifyStock = data.CreateCommand("""
            SELECT b.status, b.sellable_until, b.price_snapshot, s.status
            FROM kiosk_ops.bouquets b
            JOIN kiosk_ops.slots s ON s.bouquet_id = b.id
            WHERE b.id = @bouquet
            """);
        verifyStock.Parameters.AddWithValue("bouquet", stockedBouquetId);
        await using var reader = await verifyStock.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("STOCKED", reader.GetString(0));
        Assert.Equal(DateTime.MaxValue, reader.GetDateTime(1));
        Assert.Equal(280000L, reader.GetInt64(2));
        Assert.Equal("STOCKED", reader.GetString(3));
        await reader.CloseAsync();

        // 2. Invoke the ORIGINAL flow.expire_bouquets at a future date on an explicit transaction
        // and roll it back before subsequent checks, preventing the +50 years sweep from expiring unrelated fixtures.
        await using (var expireConn = await data.OpenConnectionAsync())
        await using (var expireTx = await expireConn.BeginTransactionAsync())
        {
            await using var expireCmd = new NpgsqlCommand("SELECT flow.expire_bouquets(CURRENT_TIMESTAMP + INTERVAL '50 years')", expireConn, expireTx);
            var expiredCount = (int)(await expireCmd.ExecuteScalarAsync())!;

            // 3. Assert the artificial bouquet was NOT expired and remains STOCKED within the transaction
            await using var checkAfterExpiry = new NpgsqlCommand("""
                SELECT b.status, s.status
                FROM kiosk_ops.bouquets b
                JOIN kiosk_ops.slots s ON s.bouquet_id = b.id
                WHERE b.id = @bouquet
                """, expireConn, expireTx);
            checkAfterExpiry.Parameters.AddWithValue("bouquet", stockedBouquetId);
            await using var readerAfter = await checkAfterExpiry.ExecuteReaderAsync();
            Assert.True(await readerAfter.ReadAsync());
            Assert.Equal("STOCKED", readerAfter.GetString(0));
            Assert.Equal("STOCKED", readerAfter.GetString(1));
            await readerAfter.CloseAsync();

            await expireTx.RollbackAsync();
        }

        // 4. Update product price and name via PUT API, then verify stocked bouquet's price_snapshot remains unchanged
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: user, sellerId: seller));
        var updateRequest = new UpdateProductRequest(
            "Hoa Lan Giả Khuyến Mãi", "Mô tả mới", 350000, ["LAN", "PROMO"], 20, 20, 30);
        var putRes = await client.PutAsJsonAsync($"/api/sellers/{seller}/products/{product}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        // Stored bouquet price snapshot must remain 280000 (unaffected by later product edits)
        await using var checkSnapshot = data.CreateCommand("""
            SELECT price_snapshot FROM kiosk_ops.bouquets WHERE id = @bouquet
            """);
        checkSnapshot.Parameters.AddWithValue("bouquet", stockedBouquetId);
        Assert.Equal(280000L, await checkSnapshot.ExecuteScalarAsync());

        // Note: Slot, bouquet, and inventory_log fixtures stay in disposable test database because inventory_logs is append-only.
    }
}
