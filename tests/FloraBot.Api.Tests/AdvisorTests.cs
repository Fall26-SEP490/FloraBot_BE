using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.Modules.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class AdvisorTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("valid", false)]
    [InlineData("valid", true)]
    [InlineData("cache", false)]
    [InlineData("foreign", false)]
    [InlineData("duplicate", false)]
    [InlineData("unsafe", false)]
    [InlineData("unavailable", false)]
    [InlineData("malformed", false)]
    [InlineData("expired", false)]
    [InlineData("timeout", false)]
    public async Task AdvisorKeepsFallbackAndChecksStockAndOwnership(string mode, bool signedIn)
    {
        var transport = new AdvisorTransport(mode);
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AI_SERVICE_URL", "http://advisor.test:8000");
            builder.UseSetting("AI_SERVICE_TOKEN", "test-service-key-at-least-thirty-two-bytes");
            builder.UseSetting("GEMINI_MODEL", mode == "cache" ? "gemini-2.5-flash-lite" : "");
            builder.ConfigureServices(services => services.AddHttpClient<AdvisorClient>().ConfigurePrimaryHttpMessageHandler(() => transport));
        });
        using var scope = app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var user = Guid.NewGuid(); var product = Guid.NewGuid();
        var kiosk = Guid.NewGuid(); var slot = Guid.NewGuid(); var customer = Guid.NewGuid(); var stranger = Guid.NewGuid();
        var key = "advisor-test-" + kiosk;
        await using var setup = source.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at,brand_tone)
            VALUES(@seller,'Advisor test','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30,'Am ap');
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@user,@user::text || '@example.invalid','!unprovisioned','Test shop','SELLER',@seller);
            INSERT INTO identity.users(id,email,full_name,role) VALUES(@customer,@customer::text || '@example.invalid','Customer','CUSTOMER'),(@stranger,@stranger::text || '@example.invalid','Other','CUSTOMER');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,description,tags)
            VALUES(@product,@seller,'Test bouquet',250000,48,'ACTIVE','Hoa tang me',ARRAY['MOTHER']);
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash)
            VALUES(@kiosk,@kiosk::text,'Advisor kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE',@hash);
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel) VALUES(@slot,@kiosk,'A01',0);
            SELECT flow.assign_slot(@slot,@seller,'10000000-0000-0000-0000-000000000001');
            """);
        foreach (var (name, id) in new[] { ("seller", seller), ("user", user), ("product", product), ("kiosk", kiosk), ("slot", slot), ("customer", customer), ("stranger", stranger) }) setup.Parameters.AddWithValue(name, id);
        setup.Parameters.AddWithValue("hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        await setup.ExecuteNonQueryAsync();
        await using var stock = source.CreateCommand("SELECT flow.stock_bouquet(flow.new_batch(),@product,@slot,@user,@qr)");
        stock.Parameters.AddWithValue("product", product); stock.Parameters.AddWithValue("slot", slot); stock.Parameters.AddWithValue("user", user); stock.Parameters.AddWithValue("qr", "advisor-" + product);
        var bouquet = (Guid)(await stock.ExecuteScalarAsync())!;
        transport.Expire = async () =>
        {
            await using var expire = source.CreateCommand("UPDATE kiosk_ops.bouquets SET sellable_until=now()-interval '1 second' WHERE id=@id");
            expire.Parameters.AddWithValue("id", bouquet); await expire.ExecuteNonQueryAsync();
        };
        using var client = app.CreateClient();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        if (signedIn) client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = customer, Role = "CUSTOMER", FullName = "Test" }, kioskId: kiosk.ToString()));
        else client.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        var input = new AiSuggestRequest(Guid.NewGuid().ToString(), "MOTHER", "41_60", "BIRTHDAY", "WARM", 300000);
        var route = $"/api/kiosks/{kiosk}/flows/ai_suggest";
        var response = await client.PostAsJsonAsync(route, input);
        response.EnsureSuccessStatusCode();
        var survey = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        var path = $"/api/kiosks/{kiosk}/surveys/{survey}";
        var read = await client.GetAsync(path);
        Assert.True(read.Headers.CacheControl?.NoStore);
        var overview = await read.Content.ReadFromJsonAsync<GiftSurveyResponse>();
        Assert.Equal(mode is "valid" or "cache" ? "LLM" : "FALLBACK", overview!.Source);
        if (mode == "expired") Assert.Empty(overview.Suggestions);
        else
        {
            var suggestion = Assert.Single(overview.Suggestions);
            Assert.Equal(bouquet, suggestion.BouquetId); Assert.Equal(product, suggestion.ProductId);
            Assert.Equal(250000, suggestion.Price);
            Assert.Equal(mode is "valid" or "cache" ? "Goi y cho me" : "Hoa tang me", suggestion.Reason);
        }
        Assert.Equal(1, transport.Calls);
        using var device = app.CreateClient(); device.DefaultRequestHeaders.Add("X-Kiosk-Key", key);
        if (signedIn) Assert.Equal(HttpStatusCode.NotFound, (await device.GetAsync(path)).StatusCode);
        using var other = app.CreateClient();
        other.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User { Id = stranger, Role = "CUSTOMER", FullName = "Other" }, kioskId: kiosk.ToString()));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(path)).StatusCode);
        using var foreign = app.CreateClient(); foreign.DefaultRequestHeaders.Add("X-Kiosk-Key", "demo-key-q1");
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsJsonAsync(route, input)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync($"/api/kiosks/40000000-0000-0000-0000-000000000001/surveys/{survey}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, input with { p_budget = -1 })).StatusCode);
        Assert.Equal(1, transport.Calls);
        var lowBudget = await client.PostAsJsonAsync(route, input with { p_budget = 200000 });
        lowBudget.EnsureSuccessStatusCode();
        var lowBudgetId = (await lowBudget.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        Assert.Empty((await client.GetFromJsonAsync<GiftSurveyResponse>($"/api/kiosks/{kiosk}/surveys/{lowBudgetId}"))!.Suggestions);
        // Cultural and budget exclusions must occur before sending candidates to the service.
        await using var tags = source.CreateCommand("UPDATE catalog.flower_products SET tags=ARRAY['WHITE'] WHERE id=@id");
        tags.Parameters.AddWithValue("id", product); await tags.ExecuteNonQueryAsync();
        var excluded = await client.PostAsJsonAsync(route, input);
        excluded.EnsureSuccessStatusCode();
        var excludedId = (await excluded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        Assert.Empty((await client.GetFromJsonAsync<GiftSurveyResponse>($"/api/kiosks/{kiosk}/surveys/{excludedId}"))!.Suggestions);
        Assert.Equal(1, transport.Calls);
        if (mode == "valid" && !signedIn)
        {
            HttpStatusCode status = HttpStatusCode.OK;
            for (var attempt = 0; attempt < 11 && status == HttpStatusCode.OK; attempt++)
                status = (await client.PostAsJsonAsync(route, input)).StatusCode;
            Assert.Equal(HttpStatusCode.TooManyRequests, status);
        }
        if (mode == "cache")
        {
            await using var restore = source.CreateCommand("UPDATE catalog.flower_products SET tags=ARRAY['MOTHER'] WHERE id=@id");
            restore.Parameters.AddWithValue("id", product); await restore.ExecuteNonQueryAsync();
            var repeat = await client.PostAsJsonAsync(route, input with { p_session = Guid.NewGuid().ToString() });
            repeat.EnsureSuccessStatusCode();
            var cachedId = (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
            Assert.NotEqual(survey, cachedId);
            Assert.Equal("CACHE", (await client.GetFromJsonAsync<GiftSurveyResponse>($"/api/kiosks/{kiosk}/surveys/{cachedId}"))!.Source);
            Assert.Equal(1, transport.Calls);
            await using var change = source.CreateCommand("UPDATE catalog.flower_products SET description='Updated description' WHERE id=@id");
            change.Parameters.AddWithValue("id", product); await change.ExecuteNonQueryAsync();
            var refreshed = await client.PostAsJsonAsync(route, input);
            refreshed.EnsureSuccessStatusCode();
            var refreshedId = (await refreshed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
            Assert.Equal("LLM", (await client.GetFromJsonAsync<GiftSurveyResponse>($"/api/kiosks/{kiosk}/surveys/{refreshedId}"))!.Source);
            Assert.Equal(2, transport.Calls);
        }
    }

    private sealed class AdvisorTransport(string mode) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Func<Task>? Expire { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("http://advisor.test:8000/suggest", request.RequestUri!.ToString());
            Assert.Equal("test-service-key-at-least-thirty-two-bytes", request.Headers.Authorization!.Parameter);
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            Assert.False(payload.TryGetProperty("customer", out _));
            var candidate = Assert.Single(payload.GetProperty("candidates").EnumerateArray());
            Assert.Equal(250000, candidate.GetProperty("price").GetInt64());
            Assert.Equal("Am ap", candidate.GetProperty("brand_tone").GetString());
            if (mode == "unavailable") return new(HttpStatusCode.ServiceUnavailable);
            if (mode == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("not JSON") };
            if (mode == "timeout") await Task.Delay(3000, ct);
            if (mode == "expired") await Expire!();
            var suggestion = new { bouquet_id = mode == "foreign" ? Guid.NewGuid() : candidate.GetProperty("bouquet_id").GetGuid(), reason = mode == "unsafe" ? "https://untrusted.example" : "Goi y cho me", card_message = "Chuc me vui" };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { suggestions = mode == "duplicate" ? new[] { suggestion, suggestion } : new[] { suggestion }, model = "gemini-2.5-flash-lite", latency_ms = 100 }) };
        }
    }
}
