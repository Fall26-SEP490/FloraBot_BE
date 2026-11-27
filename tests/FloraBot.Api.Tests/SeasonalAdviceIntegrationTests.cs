using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Modules.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class SeasonalAdviceIntegrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task SellerGetsAdviceWithoutSalesOrAiAndOnlyForTheirOwnShop()
    {
        using var upstream = new UnavailableForecast();
        using var app = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AI_SERVICE_URL", "https://advisor.example.invalid");
            builder.UseSetting("AI_SERVICE_TOKEN", "test-service-key-at-least-thirty-two-bytes");
            builder.ConfigureServices(services => services.AddHttpClient<SeasonalForecastClient>()
                .ConfigurePrimaryHttpMessageHandler(() => upstream));
        });
        var data = app.Services.GetRequiredService<NpgsqlDataSource>();
        var sellerId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        const string productName = "Bó hồng Valentine";
        const string monthQuery = "?year=2027&month=2";
        var advicePath = $"/api/sellers/{sellerId}/ai/seasonal-advice";

        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Seasonal advice test','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000d',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@actor,@actor::text||'@example.invalid','!test','Florist','SELLER',@seller);
            """);
        seed.Parameters.AddWithValue("seller", sellerId);
        seed.Parameters.AddWithValue("actor", actorId);
        await seed.ExecuteNonQueryAsync();

        try
        {
            using var client = app.CreateClient();
            using var unauthorized = await client.GetAsync(advicePath + monthQuery);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

            client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: actorId, sellerId: sellerId));
            using var foreign = await client.GetAsync($"/api/sellers/{Guid.NewGuid()}/ai/seasonal-advice{monthQuery}");
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
            using var invalid = await client.GetAsync(advicePath + "?year=2027&month=13");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            using var emptyReply = await client.GetAsync(advicePath + monthQuery);
            emptyReply.EnsureSuccessStatusCode();
            Assert.True(emptyReply.Headers.CacheControl?.NoStore);
            var empty = Assert.Single((await emptyReply.Content.ReadFromJsonAsync<SeasonalSellingSuggestion[]>())!);
            Assert.Equal(new DateOnly(2027, 2, 14), empty.Date);
            Assert.Empty(empty.MatchingProducts);

            await using var product = data.CreateCommand("""
                INSERT INTO catalog.flower_products(seller_id,name,description,price,tags,status,quantity,inventory_kind,length_cm,width_cm,height_cm,shelf_life_hours)
                VALUES(@seller,@name,'Hoa lụa',250000,ARRAY['flower:rose'],'ACTIVE',2,'ARTIFICIAL',10,10,10,48);
                """);
            product.Parameters.AddWithValue("seller", sellerId);
            product.Parameters.AddWithValue("name", productName);
            await product.ExecuteNonQueryAsync();

            var filled = Assert.Single((await client.GetFromJsonAsync<SeasonalSellingSuggestion[]>(advicePath + monthQuery))!);
            Assert.Equal(productName, Assert.Single(filled.MatchingProducts));
            Assert.Equal(0, upstream.Calls);

            var today = SeasonalCalendar.TodayVn();
            var forecastMonth = today.AddMonths(1);
            using var forecast = await client.PostAsJsonAsync($"/api/sellers/{sellerId}/ai/seasonal-forecast", new
            {
                forecastMonth.Year,
                forecastMonth.Month,
                HistoryFrom = today.AddDays(-29),
                HistoryTo = today,
            });
            forecast.EnsureSuccessStatusCode();
            var result = await forecast.Content.ReadFromJsonAsync<SeasonalForecastResponse>();
            var item = Assert.Single(result!.Products);
            Assert.Equal(productName, item.Name);
            Assert.Null(item.ForecastUnits);
            Assert.Null(item.ForecastInvoiceCount);
            Assert.Equal(0, item.ObservedUnits);
            Assert.Contains("Bạn vẫn có thể xem số đã bán và gợi ý theo dịp", Assert.Single(result.Warnings));
            Assert.Equal(1, upstream.Calls);
        }
        finally
        {
            await using var cleanup = data.CreateCommand("""
                DELETE FROM catalog.flower_products WHERE seller_id=@seller;
                DELETE FROM identity.users WHERE id=@actor;
                DELETE FROM identity.sellers WHERE id=@seller;
                """);
            cleanup.Parameters.AddWithValue("seller", sellerId);
            cleanup.Parameters.AddWithValue("actor", actorId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class UnavailableForecast : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
