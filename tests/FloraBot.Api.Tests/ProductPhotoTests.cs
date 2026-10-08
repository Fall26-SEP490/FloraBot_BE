using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FloraBot.Api.Modules.Catalog;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FloraBot.Api.Tests;

public sealed class ProductPhotoTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task AuthorizedUploadPersistsVerifiedBytesAndRejectsOtherTenantsBeforeProvider()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jN1sAAAAASUVORK5CYII=");
        using var handler = new Storage(bytes);
        using var transport = new HttpClient(handler);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CLOUDINARY_CLOUD_NAME"] = "test-cloud",
            ["CLOUDINARY_API_KEY"] = "test",
            ["CLOUDINARY_API_SECRET"] = "test-secret"
        }).Build();
        using var app = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddScoped(_ => new CloudinaryMedia(transport, settings, TimeProvider.System))));
        using var scope = app.Services.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var seller = Guid.NewGuid(); var actor = Guid.NewGuid(); var product = Guid.NewGuid();
        await using var setup = data.CreateCommand("""
            INSERT INTO identity.sellers(id,shop_name,phone,status,package_id,package_expires_at)
            VALUES(@seller,'Photo fixture','0901112233','ACTIVE','20000000-0000-0000-0000-00000000000a',CURRENT_DATE+30);
            INSERT INTO identity.users(id,email,password_hash,full_name,role,seller_id)
            VALUES(@actor,@actor::text||'@example.invalid','!unprovisioned','Photo seller','SELLER',@seller);
            INSERT INTO catalog.flower_products(id,seller_id,name,price,shelf_life_hours)
            VALUES(@product,@seller,'Photo flower',25000,48);
            """);
        setup.Parameters.AddWithValue("seller", seller); setup.Parameters.AddWithValue("actor", actor); setup.Parameters.AddWithValue("product", product);
        await setup.ExecuteNonQueryAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", factory.Token(userId: actor, sellerId: seller));
        var own = $"/api/sellers/{seller}/products/{product}/photos";
        var foreign = own.Replace(product.ToString(), "30000000-0000-0000-0000-000000000003");
        HttpContent Content(byte[]? value = null, string mime = "image/png")
        {
            var result = new ByteArrayContent(value ?? bytes);
            result.Headers.ContentType = new MediaTypeHeaderValue(mime);
            return result;
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(foreign, Content())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(own.Replace(seller.ToString(), "20000000-0000-0000-0000-000000000002"), Content())).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync(own, Content(mime: "image/svg+xml"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(own, Content("not an image"u8.ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(own, Content(mime: "image/jpeg"))).StatusCode);
        Assert.Equal(0, handler.Uploads);
        var response = await client.PostAsync(own, Content());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var photo = (await response.Content.ReadFromJsonAsync<ProductPhotoResponse>())!;
        Assert.Equal(bytes.Length, photo.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), photo.Sha256);
        await using var verify = data.CreateCommand("SELECT file_url=@url AND size_bytes=@size AND sha256=@hash AND mime_type='image/png' AND uploaded_by=@actor FROM notify.attachments WHERE id=@id");
        verify.Parameters.AddWithValue("actor", actor); verify.Parameters.AddWithValue("url", photo.Url); verify.Parameters.AddWithValue("size", bytes.Length);
        verify.Parameters.AddWithValue("hash", photo.Sha256); verify.Parameters.AddWithValue("id", photo.Id);
        Assert.Equal(true, await verify.ExecuteScalarAsync());
        var galleryResponse = await client.GetAsync(own);
        Assert.True(galleryResponse.Headers.CacheControl?.NoStore);
        var gallery = (await galleryResponse.Content.ReadFromJsonAsync<ProductPhotoPage>())!;
        Assert.Equal(photo, Assert.Single(gallery.Items));
        Assert.False(gallery.HasMore);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(foreign)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(own + "?page=0")).StatusCode);
        await using var fixtures = data.CreateCommand("""
            INSERT INTO notify.attachments(owner_service,owner_type,owner_id,file_url,mime_type,size_bytes,sha256,phase)
            SELECT 'catalog','flower_product',@product,'https://example.invalid/'||n||'.png','image/png',100,'fixture','PRODUCT'
            FROM generate_series(1,22) n;
            INSERT INTO notify.attachments(owner_service,owner_type,owner_id,file_url,mime_type,size_bytes,sha256,phase)
            VALUES('ordering','flower_product',@product,'https://example.invalid/private.png','image/png',100,'fixture','EVIDENCE');
            """);
        fixtures.Parameters.AddWithValue("product", product);
        await fixtures.ExecuteNonQueryAsync();
        var first = (await client.GetFromJsonAsync<ProductPhotoPage>(own + "?page=1"))!;
        var second = (await client.GetFromJsonAsync<ProductPhotoPage>(own + "?page=2"))!;
        Assert.Equal(20, first.Items.Count); Assert.True(first.HasMore);
        Assert.Equal(3, second.Items.Count); Assert.False(second.HasMore);
        Assert.Equal(23, first.Items.Concat(second.Items).Select(x => x.Id).Distinct().Count());
        Assert.DoesNotContain(first.Items.Concat(second.Items), x => x.Url.Contains("private"));
        var flow = $"/api/sellers/{seller}/flows/add_product_photo";
        var downloads = handler.Downloads;
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(flow,
            new { p_product = "30000000-0000-0000-0000-000000000003", p_url = photo.Url })).StatusCode);
        foreach (var invalid in new[] { "http://127.0.0.1/private.png", photo.Url.Replace("test-cloud", "other-cloud"), photo.Url + "?transform=1", photo.Url.Replace("/upload/", "/upload/w_100/") })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(flow, new { p_product = product, p_url = invalid })).StatusCode);
        Assert.Equal(downloads, handler.Downloads);
        var linked = await client.PostAsJsonAsync(flow, new { p_product = product, p_url = photo.Url });
        Assert.True(linked.IsSuccessStatusCode, await linked.Content.ReadAsStringAsync());
        var linkedId = (await linked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetGuid();
        verify.Parameters["id"].Value = linkedId;
        Assert.Equal(true, await verify.ExecuteScalarAsync());
        Assert.Equal(downloads + 1, handler.Downloads);
        handler.DownloadStatus = HttpStatusCode.Found;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync(flow, new { p_product = product, p_url = photo.Url })).StatusCode);
        handler.DownloadStatus = HttpStatusCode.OK;
        await using var countPhotos = data.CreateCommand("SELECT count(*) FROM notify.attachments WHERE owner_service='catalog' AND owner_id=@product");
        countPhotos.Parameters.AddWithValue("product", product);
        Assert.Equal(24L, await countPhotos.ExecuteScalarAsync());
        handler.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync(own, Content())).StatusCode);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(own, Content())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(own)).StatusCode);
        Assert.Equal(2, handler.Uploads);
        await using var expire = data.CreateCommand("UPDATE identity.sellers SET status='PAST_DUE' WHERE id=@seller");
        expire.Parameters.AddWithValue("seller", seller);
        await expire.ExecuteNonQueryAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(own, Content())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(own)).StatusCode);
        Assert.Equal(2, handler.Uploads);
    }

    private sealed class Storage(byte[] bytes) : HttpMessageHandler
    {
        public int Uploads { get; private set; }
        public int Downloads { get; private set; }
        public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.OK;
        public bool Fail { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                Downloads++;
                return new(DownloadStatus) { Content = new ByteArrayContent(bytes) };
            }
            Uploads++;
            if (Fail) return new(HttpStatusCode.ServiceUnavailable);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            var id = await form.Single(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "public_id").ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { public_id = id, secure_url = $"https://res.cloudinary.com/test-cloud/image/upload/v1/{id}.png" })) };
        }
    }
}
