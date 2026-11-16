using System.Net;
using System.Net.Http.Json;
using FloraBot.Api.Auth;
using FloraBot.Api.Data.Entities;
using FloraBot.Api.ReadModels;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class FlowerReviewTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task OnlyCompletedPreordersCanReviewAndShopAverageUsesIndividualReviews()
    {
        var data = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var ids = new[] { "seller", "kiosk", "slot", "first", "second", "buyer", "preorder", "other", "stranger", "firstReviewer", "bouquet", "walkOrder", "preOrder", "firstReviewOrder", "otherOrder", "unpaid" }
            .ToDictionary(key => key, _ => Guid.NewGuid());
        await using var seed = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Shop review','0901234567','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role)
            SELECT x,x::text||'@example.invalid','!test','Test review buyer','CUSTOMER'
            FROM unnest(ARRAY[@buyer,@preorder,@other,@stranger,@firstReviewer]) AS x;
            INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status)
            VALUES(@kiosk,@kiosk::text,'Review kiosk','Test address','HCM',@kiosk::text,@kiosk::text,'ONLINE');
            INSERT INTO kiosk_ops.slots(id,kiosk_id,slot_code,relay_channel,zone)
            VALUES(@slot,@kiosk,'R01',0,'WALK_IN');
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours,status,quantity)
            VALUES(@first,@seller,'First flower',350000,48,'ACTIVE',1),
                  (@second,@seller,'Second flower',350000,48,'ACTIVE',1);
            INSERT INTO ordering.orders(id,order_code,checkout_id,customer_id,seller_id,kiosk_id,status,subtotal,total_amount,tracking_token,completed_at)
            SELECT id,id::text,id,buyer,@seller,@kiosk,status,350000,350000,id::text,
                   CASE WHEN status = 'COMPLETED' THEN now() END
            FROM (VALUES (@walkOrder,@buyer,'COMPLETED'),(@preOrder,@preorder,'COMPLETED'),
                         (@firstReviewOrder,@firstReviewer,'COMPLETED'),
                         (@otherOrder,@other,'COMPLETED'),(@unpaid,@stranger,'PAID')) fixture(id,buyer,status);
            INSERT INTO kiosk_ops.bouquets(id,product_id,seller_id,qr_code,price_snapshot,status,stocked_at,sellable_until)
            VALUES(@bouquet,@first,@seller,@bouquet::text,350000,'PICKED_UP',now(),now()+interval '48 hours');
            INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
            VALUES(@walkOrder,'BOUQUET',@bouquet,@slot,'First flower',1,350000,350000);
            INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state,order_id)
            SELECT gen_random_uuid(),buyer,@seller,product,@kiosk,'CUSTOM','Fixture review request',
                   now()-interval '5 minutes',now()+interval '2 hours','ORDERED',id
            FROM (VALUES (@preOrder,@preorder,@first),(@firstReviewOrder,@firstReviewer,@first),
                         (@otherOrder,@other,@second),(@unpaid,@stranger,@first)) fixture(id,buyer,product);
            """);
        foreach (var (name, id) in ids) seed.Parameters.AddWithValue(name, id);
        await seed.ExecuteNonQueryAsync();

        using var anonymous = factory.CreateClient();
        using var popularShop = await anonymous.PostAsJsonAsync("/api/shop/flower-chat",
            new { question = "shop nào bán dc nhiều đơn nhỉ" });
        popularShop.EnsureSuccessStatusCode();
        var shopAnswer = await popularShop.Content.ReadFromJsonAsync<FloraBot.Api.Modules.Ai.FlowerChatResponse>();
        Assert.Equal("FLOWERS", shopAnswer!.Intent);
        Assert.Contains("dẫn đầu về số đơn đặt trước đã hoàn tất", shopAnswer.Message);
        Assert.Empty(shopAnswer.Suggestions);
        using var buyer = Member(ids["buyer"]);
        using var firstReviewer = Member(ids["firstReviewer"]);
        using var preorder = Member(ids["preorder"]);
        using var other = Member(ids["other"]);
        using var stranger = Member(ids["stranger"]);
        var firstPath = $"/api/shop/products/{ids["first"]}";
        var secondPath = $"/api/shop/products/{ids["second"]}";
        var empty = await anonymous.GetFromJsonAsync<FlowerReviewPage>(firstPath + "/reviews");
        Assert.NotNull(empty);
        Assert.Null(empty.ShopAverage);
        Assert.Empty(empty.Items);
        Assert.False((await buyer.GetFromJsonAsync<FlowerReviewEligibility>(firstPath + "/review-eligibility"))!.Eligible);
        Assert.True((await firstReviewer.GetFromJsonAsync<FlowerReviewEligibility>(firstPath + "/review-eligibility"))!.Eligible);
        Assert.False((await stranger.GetFromJsonAsync<FlowerReviewEligibility>(firstPath + "/review-eligibility"))!.Eligible);
        await Post(anonymous, firstPath, 4, HttpStatusCode.Unauthorized);
        await Post(stranger, firstPath, 4, HttpStatusCode.Conflict);
        await Post(buyer, firstPath, 4, HttpStatusCode.Conflict);
        await Post(firstReviewer, secondPath, 4, HttpStatusCode.Conflict);
        await Post(firstReviewer, firstPath, 6, HttpStatusCode.BadRequest);
        await Post(firstReviewer, firstPath, 3, HttpStatusCode.BadRequest, "not-base64");

        const string image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aCfUAAAAASUVORK5CYII=";
        await Post(firstReviewer, firstPath, 3, HttpStatusCode.Created, image);
        await Post(firstReviewer, firstPath, 5, HttpStatusCode.Conflict);
        await Post(preorder, firstPath, 5, HttpStatusCode.Created);
        await Post(other, secondPath, 5, HttpStatusCode.Created);
        Assert.False((await firstReviewer.GetFromJsonAsync<FlowerReviewEligibility>(firstPath + "/review-eligibility"))!.Eligible);

        var page = await anonymous.GetFromJsonAsync<FlowerReviewPage>(firstPath + "/reviews");
        Assert.NotNull(page);
        Assert.Equal(4m, page.ProductAverage);
        Assert.Equal(2, page.ProductCount);
        Assert.Equal(3, page.ShopCount);
        Assert.Equal(4.333m, Math.Round(page.ShopAverage!.Value, 3));
        var photoReview = Assert.Single(page.Items, x => x.PhotoUrl is not null);
        Assert.Equal("Hoa đẹp.", photoReview.Comment);
        using var photo = await anonymous.GetAsync(photoReview.PhotoUrl);
        Assert.Equal("image/png", photo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Convert.FromBase64String(image), await photo.Content.ReadAsByteArrayAsync());
        using var wrongProduct = await anonymous.GetAsync($"{secondPath}/reviews/{photoReview.Id}/photo");
        Assert.Equal(HttpStatusCode.NotFound, wrongProduct.StatusCode);
    }

    private HttpClient Member(Guid id)
    {
        var client = factory.CreateClient();
        var tokens = factory.Services.GetRequiredService<TokenService>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.Issue(new User
            { Id = id, Role = "CUSTOMER", FullName = "Test review buyer" }));
        return client;
    }

    private static async Task Post(HttpClient client, string product, int stars, HttpStatusCode expected, string? image = null)
    {
        using var response = await client.PostAsJsonAsync(product + "/reviews", new FlowerReviewInput(stars, " Hoa đẹp. ", image));
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
