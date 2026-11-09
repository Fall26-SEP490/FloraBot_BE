using System.Net;
using System.Net.Http.Json;
using System.Text;
using FloraBot.Api.Infrastructure;
using FloraBot.Api.Modules.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ProductQuantityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task CreateReadAndUpdateRoundtripShopQuantityIncludingZeroAndIntMaxValue()
    {
        var f = await Setup();
        using var client = Client(f);
        var product = await Create(client, f, 8);
        var untouchedProduct = await Create(client, f, 19);

        var created = await Read(client, product);
        Assert.Equal(8, created.Quantity);
        Assert.Equal(8, await StoredQuantity(product));
        Assert.Equal("Quantity flower", created.Name);
        Assert.Null(created.Description);
        Assert.Equal(125000L, created.Price);
        Assert.Equal(new[] { "ROSE", "GIFT" }, created.Tags);
        Assert.Equal("DRAFT", created.Status);
        Assert.Equal("ARTIFICIAL", created.InventoryKind);
        Assert.Null(created.ShelfLifeHours);
        Assert.Equal(15.25m, created.LengthCm);
        Assert.Equal(12m, created.WidthCm);
        Assert.Equal(30m, created.HeightCm);

        foreach (var quantity in new[] { int.MaxValue, 0 })
        {
            var response = await client.PutAsJsonAsync(Route(f, product),
                new UpdateProductRequest("Updated quantity flower", "Updated description", 145000,
                    ["ROSE", "NEW"], 15.25m, 12m, 30m, Quantity: quantity));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(product, (await response.Content.ReadFromJsonAsync<ProductIdResponse>())!.Id);
            var updated = await Read(client, product);
            Assert.Equal(quantity, updated.Quantity);
            Assert.Equal(quantity, await StoredQuantity(product));
            Assert.Equal("Updated quantity flower", updated.Name);
            Assert.Equal("Updated description", updated.Description);
            Assert.Equal(145000L, updated.Price);
            Assert.Equal(new[] { "ROSE", "NEW" }, updated.Tags);
            Assert.Equal("DRAFT", updated.Status);
            Assert.Equal("ARTIFICIAL", updated.InventoryKind);
            Assert.Null(updated.ShelfLifeHours);
            Assert.Equal(15.25m, updated.LengthCm);
            Assert.Equal(12m, updated.WidthCm);
            Assert.Equal(30m, updated.HeightCm);
            Assert.Equal(19, await StoredQuantity(untouchedProduct));
        }
    }

    [Fact]
    public async Task LegacyCreateDefaultsToZeroAndUpdateWithoutQuantityPreservesExistingStock()
    {
        var f = await Setup();
        using var client = Client(f);
        // Anonymous bodies prove that the field is omitted from JSON, rather than sent as a default value.
        var response = await client.PostAsJsonAsync("/api/me/products", new
        {
            kioskId = f.Kiosk, name = "Legacy flower", description = (string?)null,
            price = 125000L, tags = new[] { "ROSE" }, lengthCm = 15.25m, widthCm = 12m, heightCm = 30m
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = (await response.Content.ReadFromJsonAsync<ProductIdResponse>())!.Id;
        Assert.Equal(0, (await Read(client, product)).Quantity);
        Assert.Equal(0, await StoredQuantity(product));

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(Route(f, product),
            new UpdateProductRequest("Legacy flower", null, 125000, ["ROSE"], 15.25m, 12m, 30m, Quantity: 17))).StatusCode);
        var update = await client.PutAsJsonAsync(Route(f, product), new
        {
            name = "Legacy metadata edit", description = "New text", price = 150000L,
            tags = new[] { "ROSE", "GIFT" }, lengthCm = 15.25m, widthCm = 12m, heightCm = 30m
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var row = await Read(client, product);
        Assert.Equal(17, row.Quantity);
        Assert.Equal(17, await StoredQuantity(product));
        Assert.Equal("Legacy metadata edit", row.Name);
        Assert.Equal(150000L, row.Price);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public async Task InvalidQuantityRejectsCreateAndUpdateWithoutPersistingMetadata(string quantityJson)
    {
        var f = await Setup();
        using var client = Client(f);
        var product = await Create(client, f, 6);

        using var createBody = Json($$"""
            {"kioskId":"{{f.Kiosk}}","name":"Invalid create","description":null,"price":250000,
             "tags":["INVALID"],"lengthCm":10,"widthCm":10,"heightCm":10,"quantity":{{quantityJson}}}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/me/products", createBody)).StatusCode);
        using var updateBody = Json($$"""
            {"name":"Invalid edit","description":"Must not persist","price":250000,
             "tags":["INVALID"],"lengthCm":10,"widthCm":10,"heightCm":10,"quantity":{{quantityJson}}}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(Route(f, product), updateBody)).StatusCode);

        var rows = (await client.GetFromJsonAsync<List<ProductResponse>>("/api/me/products"))!;
        var unchanged = Assert.Single(rows);
        Assert.Equal(product, unchanged.Id);
        Assert.Equal(6, unchanged.Quantity);
        Assert.Equal(6, await StoredQuantity(product));
        Assert.Equal("Quantity flower", unchanged.Name);
        Assert.Null(unchanged.Description);
        Assert.Equal(125000L, unchanged.Price);
        Assert.Equal(new[] { "ROSE", "GIFT" }, unchanged.Tags);
        Assert.Equal(15.25m, unchanged.LengthCm);
    }

    [Fact]
    public async Task QuantityDoesNotBypassRequiredKioskAndDimensions()
    {
        var f = await Setup();
        using var client = Client(f);
        foreach (var body in new object[]
        {
            new { name = "No kiosk", description = (string?)null, price = 125000,
                tags = new[] { "ROSE" }, lengthCm = 10, widthCm = 10, heightCm = 10, quantity = 5 },
            new { kioskId = Guid.Empty, name = "Empty kiosk", description = (string?)null, price = 125000,
                tags = new[] { "ROSE" }, lengthCm = 10, widthCm = 10, heightCm = 10, quantity = 5 },
            new { kioskId = f.Kiosk, name = "No dimensions", description = (string?)null,
                price = 125000, tags = new[] { "ROSE" }, quantity = 5 },
            new { kioskId = f.Kiosk, name = "Invalid dimensions", description = (string?)null,
                price = 125000, tags = new[] { "ROSE" }, lengthCm = 0.004m, widthCm = 10, heightCm = 10, quantity = 5 }
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/me/products", body)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<ProductResponse>>("/api/me/products"))!);

        var product = await Create(client, f, 9);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Route(f, product), new
        {
            name = "No dimensions edit", description = (string?)null,
            price = 125000, tags = new[] { "ROSE" }, quantity = 3
        })).StatusCode);
        Assert.Equal(9, await StoredQuantity(product));
    }

    [Fact]
    public async Task ForeignKioskProductAndSellerRoutesCannotChangeQuantity()
    {
        var own = await Setup();
        var foreign = await Setup();
        using var ownClient = Client(own);
        using var foreignClient = Client(foreign);
        var ownProduct = await Create(ownClient, own, 7);
        var foreignProduct = await Create(foreignClient, foreign, 21);
        var write = new UpdateProductRequest("Cross tenant edit", null, 50000, ["ROSE"], 15.25m, 12m, 30m, Quantity: 100);

        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.PostAsJsonAsync("/api/me/products", new CreateProductRequest(
                foreign.Kiosk, "Foreign kiosk", null, 50000, ["ROSE"], 10, 10, 10, Quantity: 100))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.PutAsJsonAsync(Route(own, foreignProduct), write)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.PutAsJsonAsync(Route(foreign, foreignProduct), write)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownClient.PostAsJsonAsync($"/api/sellers/{foreign.Seller}/products", new CreateProductRequest(
                own.Kiosk, "Legacy route", null, 50000, ["ROSE"], 10, 10, 10, Quantity: 100))).StatusCode);

        Assert.Equal(7, await StoredQuantity(ownProduct));
        Assert.Equal(21, await StoredQuantity(foreignProduct));
        Assert.Equal(ownProduct, Assert.Single((await ownClient.GetFromJsonAsync<List<ProductResponse>>("/api/me/products"))!).Id);
        Assert.Equal(21, (await Read(foreignClient, foreignProduct)).Quantity);
    }

    [Fact]
    public async Task LiveBouquetCountDoesNotReplaceShopQuantityAndConflictingEditLeavesItUnchanged()
    {
        var f = await Setup();
        using var client = Client(f);
        var product = await Create(client, f, 11);
        await Execute(f, """
            INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,stocked_at,sellable_until)
            VALUES(@bouquet,@product,@seller,@bouquet::text,125000,'STOCKED',public.app_now(),'infinity');
            UPDATE kiosk_ops.slots SET status='STOCKED',bouquet_id=@bouquet WHERE id=@slot;
            """, product);
        // This is a read fixture, not a stock operation: one bouquet is in the kiosk and eleven remain at the shop.
        Assert.Equal(11, (await Read(client, product)).Quantity);
        Assert.Equal(11, await StoredQuantity(product));

        var response = await client.PutAsJsonAsync(Route(f, product), new UpdateProductRequest(
            "Conflicting edit", "Must not persist", 200000, ["OTHER"], 20, 12, 30, Quantity: 4));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var unchanged = await Read(client, product);
        Assert.Equal(11, unchanged.Quantity);
        Assert.Equal(11, await StoredQuantity(product));
        Assert.Equal("Quantity flower", unchanged.Name);
        Assert.Null(unchanged.Description);
        Assert.Equal(125000L, unchanged.Price);
        Assert.Equal(new[] { "ROSE", "GIFT" }, unchanged.Tags);
        Assert.Equal(15.25m, unchanged.LengthCm);
    }

    private sealed record Fixture(Guid Seller, Guid Actor, Guid Kiosk, Guid Slot, Guid Bouquet);

    private async Task<Fixture> Setup()
    {
        var f = new Fixture(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Execute(f, """
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Quantity fixture','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+365);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id,status)
            VALUES(@actor,@actor::text||'@example.invalid','!unprovisioned','Quantity seller','SELLER',@seller,'ACTIVE');
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Quantity kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone)
            VALUES(@slot,@kiosk,'A01',0,'WALK_IN');
            """);
        using var scope = factory.Services.CreateScope();
        await CurrentMainKioskFixture.AssignKioskWithLeaseAsync(
            scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>(), f.Kiosk, f.Seller);
        // Paid leases and append-only audit rows remain in the disposable integration database.
        return f;
    }

    private async Task Execute(Fixture f, string sql, Guid? product = null)
    {
        using var scope = factory.Services.CreateScope();
        await using var command = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        foreach (var (name, id) in new[] { ("seller", f.Seller), ("actor", f.Actor), ("kiosk", f.Kiosk),
            ("slot", f.Slot), ("bouquet", f.Bouquet), ("product", product ?? Guid.Empty) })
            command.Parameters.AddWithValue(name, id);
        await command.ExecuteNonQueryAsync();
    }

    private HttpClient Client(Fixture f)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: f.Actor, sellerId: f.Seller));
        return client;
    }

    private static async Task<Guid> Create(HttpClient client, Fixture f, int quantity)
    {
        var response = await client.PostAsJsonAsync("/api/me/products", new CreateProductRequest(
            f.Kiosk, "Quantity flower", null, 125000, ["ROSE", "GIFT"], 15.25m, 12m, 30m, Quantity: quantity));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductIdResponse>())!.Id;
    }

    private static async Task<ProductResponse> Read(HttpClient client, Guid product)
        => Assert.Single((await client.GetFromJsonAsync<List<ProductResponse>>("/api/me/products"))!, p => p.Id == product);

    private async Task<int> StoredQuantity(Guid product)
    {
        using var scope = factory.Services.CreateScope();
        await using var command = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("SELECT quantity FROM catalog.flower_products WHERE id=@product");
        command.Parameters.AddWithValue("product", product);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
    private static string Route(Fixture f, Guid product) => $"/api/sellers/{f.Seller}/products/{product}";
}
