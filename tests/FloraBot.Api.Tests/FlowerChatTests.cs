using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FloraBot.Api.Modules.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class FlowerChatTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("WALK_IN")]
    [InlineData("PREORDER")]
    public async Task ChatUsesBoundedOffersAndSuppressesOffTopicModelContent(string mode)
    {
        using var transport = new OffTopicTransport(mode);
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AI_SERVICE_URL", "http://advisor.test:8000");
            builder.UseSetting("AI_SERVICE_TOKEN", "test-service-key-at-least-thirty-two-bytes");
            builder.ConfigureServices(services => services.AddHttpClient<FlowerChat>()
                .ConfigurePrimaryHttpMessageHandler(() => transport));
        });
        var data = app.Services.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var kiosk = Guid.NewGuid();
        var product = Guid.NewGuid();
        var slot = Guid.NewGuid();
        var name = $"Taylorflower-{product:N}";
        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Flower chat shop','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000d',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@actor,@actor::text||'@example.invalid','!test','Florist','SELLER',@seller);
            INSERT INTO catalog.flower_products(id,seller_id,name,description,price,tags,status,quantity,inventory_kind,length_cm,width_cm,height_cm,shelf_life_hours)
            VALUES(@product,@seller,@name,'Hoa lụa màu hồng',250000,ARRAY['flower:rose','color:pink'],'ACTIVE',2,'ARTIFICIAL',10,10,10,48);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,latitude,longitude)
            VALUES(@kiosk,@kiosk::text,'Chat kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',10.8,106.6);
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone) VALUES
            (@slot,@kiosk,'A01',0,'WALK_IN'),(gen_random_uuid(),@kiosk,'A02',1,'PREORDER');
            """ + "\n" + CurrentMainKioskFixture.AssignKioskWithLeaseSql);
        foreach (var (parameter, value) in new[] { ("seller", seller), ("actor", actor), ("kiosk", kiosk), ("product", product), ("slot", slot) })
            seed.Parameters.AddWithValue(parameter, value);
        seed.Parameters.AddWithValue("name", name);
        await seed.ExecuteNonQueryAsync();
        await using var stock = data.CreateCommand("SELECT flow.stock_bouquet(flow.new_batch(),@product,@slot,@actor,@qr)");
        stock.Parameters.AddWithValue("product", product);
        stock.Parameters.AddWithValue("slot", slot);
        stock.Parameters.AddWithValue("actor", actor);
        stock.Parameters.AddWithValue("qr", name);
        var bouquet = (Guid)(await stock.ExecuteScalarAsync())!;

        using var client = app.CreateClient();
        const string path = "/api/shop/flower-chat";
        using var response = await client.PostAsJsonAsync(path, new FlowerChatInput("Hỏi ngoài chủ đề", mode, 300000, kiosk));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var answer = await response.Content.ReadFromJsonAsync<FlowerChatResponse>();
        Assert.Equal("OFF_TOPIC", answer!.Intent);
        Assert.StartsWith("FloraBot", answer.Message);
        Assert.Empty(answer.Suggestions);
        Assert.Equal(1, transport.Calls);
        transport.Expected = mode == "WALK_IN" ? bouquet : product;
        using var grounded = await client.PostAsJsonAsync(path, new FlowerChatInput($"Tìm {name} dưới 300k", mode, KioskId: kiosk));
        Assert.True(grounded.IsSuccessStatusCode, await grounded.Content.ReadAsStringAsync());
        var result = await grounded.Content.ReadFromJsonAsync<FlowerChatResponse>();
        var card = Assert.Single(result!.Suggestions);
        Assert.Equal(product, card.ProductId);
        Assert.Equal(kiosk, card.KioskId);
        Assert.Equal(250000, card.Price);
        Assert.Equal(mode, card.Mode);
        using var invalid = await client.PostAsJsonAsync(path, new FlowerChatInput("Hoa", "OTHER", 1));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(2, transport.Calls);
        if (mode == "WALK_IN")
        {
            using var missingKiosk = await client.PostAsJsonAsync(path, new FlowerChatInput("Mua tại tủ"));
            var missing = await missingKiosk.Content.ReadFromJsonAsync<FlowerChatResponse>();
            Assert.Contains("chọn kiosk", missing!.Message);
            Assert.Empty(missing.Suggestions);
            Assert.Equal(2, transport.Calls);

            transport.Expected = null;
            transport.ExpectEmpty = true;
            using var wrongKiosk = await client.PostAsJsonAsync(path,
                new FlowerChatInput($"Mua tại tủ {name}", KioskId: Guid.NewGuid()));
            Assert.True(wrongKiosk.IsSuccessStatusCode);
            Assert.Equal(3, transport.Calls);
        }
    }

    private sealed class OffTopicTransport(string mode) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Guid? Expected { get; set; }
        public bool ExpectEmpty { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("/flower-chat", request.RequestUri!.AbsolutePath);
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal("customer", payload.GetProperty("audience").GetString());
            var candidates = payload.GetProperty("candidates");
            if (ExpectEmpty) Assert.Equal(0, candidates.GetArrayLength());
            Assert.True(candidates.GetArrayLength() <= 30);
            foreach (var item in candidates.EnumerateArray())
            {
                Assert.Equal(mode, item.GetProperty("mode").GetString());
                Assert.InRange(item.GetProperty("price").GetInt64(), 1, 300000);
                Assert.True(item.TryGetProperty("tags", out _));
                Assert.True(item.TryGetProperty("description", out _));
            }
            if (Expected is Guid id)
            {
                var candidate = Assert.Single(candidates.EnumerateArray(), c => c.GetProperty("id").GetGuid() == id);
                Assert.Equal("Hoa lụa màu hồng", candidate.GetProperty("description").GetString());
                Assert.Contains("flower:rose", candidate.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { intent = "FLOWERS", message = "FloraBot tìm thấy mẫu phù hợp.", suggestions = new[] { new { id, reason = "Bó hồng phù hợp." } } })
                };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { intent = "OFF_TOPIC", message = "Untrusted model message", suggestions = Array.Empty<object>() }) };
        }
    }
}
