using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloraBot.Api.Modules.Notify;
using Microsoft.Extensions.Configuration;

namespace FloraBot.Api.Tests;

public sealed class CloudinaryMediaTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jN1sAAAAASUVORK5CYII=");
    private static IConfiguration Settings(bool configured = true) => new ConfigurationBuilder().AddInMemoryCollection(
        configured ? new Dictionary<string, string?> { ["CLOUDINARY_CLOUD_NAME"] = "test-cloud", ["CLOUDINARY_API_KEY"] = "test-key", ["CLOUDINARY_API_SECRET"] = "test-secret" } : []).Build();

    [Fact]
    public async Task UploadSignsRequestAndVerifiesStoredBytes()
    {
        using var handler = new Provider();
        using var http = new HttpClient(handler);
        var result = await new CloudinaryMedia(http, Settings(), TimeProvider.System).UploadAsync(Png, default);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(Png.Length, result.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), result.Sha256);
        Assert.StartsWith("https://res.cloudinary.com/test-cloud/image/upload/v1/florabot/", result.Url);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("authenticated-signed")]
    [InlineData("authenticated-unsigned")]
    public async Task UploadAuthenticatedAcceptsCanonicalSignedAndUnsignedUrls(string mode)
    {
        using var handler = new Provider(mode);
        using var http = new HttpClient(handler);
        var result = await new CloudinaryMedia(http, Settings(), TimeProvider.System).UploadAsync(Png, default, authenticated: true);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(Png.Length, result.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Png)), result.Sha256);
        Assert.Contains("/image/authenticated/", result.Url);
        Assert.StartsWith("florabot-evidence/", result.PublicId);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("authenticated-public-url")]
    [InlineData("authenticated-transformation")]
    [InlineData("authenticated-wrong-host")]
    [InlineData("authenticated-invalid-signature")]
    public async Task UploadAuthenticatedRejectsPublicTransformationsAndForeignHosts(string mode)
    {
        using var handler = new Provider(mode);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<MediaUnavailableException>(() => new CloudinaryMedia(http, Settings(), TimeProvider.System).UploadAsync(Png, default, authenticated: true));
    }

    [Theory]
    [InlineData("wrong-host")]
    [InlineData("wrong-cloud")]
    [InlineData("wrong-id")]
    [InlineData("query")]
    [InlineData("upload-failure")]
    [InlineData("redirect")]
    [InlineData("corrupt")]
    [InlineData("oversized")]
    public async Task DoesNotTrustProviderMetadataOrChangedContent(string mode)
    {
        using var handler = new Provider(mode);
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<MediaUnavailableException>(() => new CloudinaryMedia(http, Settings(), TimeProvider.System).UploadAsync(Png, default));
        Assert.Equal(mode is "corrupt" or "oversized" ? 2 : 1, handler.Calls);
    }

    [Fact]
    public async Task MissingConfigurationAndInvalidFilesMakeNoNetworkRequests()
    {
        using var handler = new Provider();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<MediaUnavailableException>(() => new CloudinaryMedia(http, Settings(false), TimeProvider.System).UploadAsync(Png, default));
        var service = new CloudinaryMedia(http, Settings(), TimeProvider.System);
        foreach (var bytes in new[] { Array.Empty<byte>(), "<svg></svg>"u8.ToArray(), new byte[CloudinaryMedia.MaximumBytes + 1] })
            await Assert.ThrowsAsync<ArgumentException>(() => service.UploadAsync(bytes, default));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Provider(string mode = "valid") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.Host == "api.cloudinary.com")
                {
                    Assert.EndsWith("/image/download", request.RequestUri.AbsolutePath);
                    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                    Assert.Equal("authenticated", query["type"]);
                    var canonical = string.Join("&", query.Where(x => x.Key is not ("api_key" or "signature")).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
                    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical + "test-secret"))), query["signature"].ToString());
                    var content = new ByteArrayContent(mode == "corrupt" ? "different"u8.ToArray() : Png);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }
                Assert.Equal("res.cloudinary.com", request.RequestUri!.Host);
                var storedContent = new ByteArrayContent(mode == "corrupt" ? "different"u8.ToArray() : Png);
                if (mode == "oversized") storedContent.Headers.ContentLength = CloudinaryMedia.MaximumBytes + 1;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = storedContent };
            }
            Assert.Equal("https://api.cloudinary.com/v1_1/test-cloud/image/upload", request.RequestUri!.AbsoluteUri);
            var parts = Assert.IsType<MultipartFormDataContent>(request.Content);
            var fields = new Dictionary<string, string>();
            foreach (var part in parts)
            {
                var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                if (name == "file")
                {
                    Assert.Equal(Png, await part.ReadAsByteArrayAsync(cancellationToken));
                    Assert.Equal("image/png", part.Headers.ContentType!.MediaType);
                }
                else fields[name] = await part.ReadAsStringAsync(cancellationToken);
            }
            Assert.Equal("test-key", fields["api_key"]);
            Assert.Equal("false", fields["overwrite"]);
            var isAuthenticated = fields.TryGetValue("type", out var deliveryType) && deliveryType == "authenticated";
            if (isAuthenticated)
            {
                Assert.Matches(new Regex("^florabot-evidence/[0-9a-f]{32}$"), fields["public_id"]);
                var authSig = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"overwrite=false&public_id={fields["public_id"]}&timestamp={fields["timestamp"]}&type=authenticatedtest-secret")));
                Assert.Equal(authSig, fields["signature"]);
            }
            else
            {
                Assert.Matches(new Regex("^florabot/[0-9a-f]{32}$"), fields["public_id"]);
                var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"overwrite=false&public_id={fields["public_id"]}&timestamp={fields["timestamp"]}test-secret")));
                Assert.Equal(signature, fields["signature"]);
            }
            Assert.DoesNotContain("test-secret", await parts.ReadAsStringAsync(cancellationToken));
            if (mode == "upload-failure") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (mode == "redirect") return new HttpResponseMessage(HttpStatusCode.Redirect);
            string url;
            if (isAuthenticated)
            {
                url = mode switch
                {
                    "authenticated-signed" => $"https://res.cloudinary.com/test-cloud/image/authenticated/s--Ab12-_34--/v1/{fields["public_id"]}.png",
                    "authenticated-public-url" => $"https://res.cloudinary.com/test-cloud/image/upload/v1/{fields["public_id"]}.png",
                    "authenticated-transformation" => $"https://res.cloudinary.com/test-cloud/image/authenticated/c_fill,w_100/v1/{fields["public_id"]}.png",
                    "authenticated-wrong-host" => $"https://foreign.example.invalid/test-cloud/image/authenticated/v1/{fields["public_id"]}.png",
                    "authenticated-invalid-signature" => $"https://res.cloudinary.com/test-cloud/image/authenticated/s--short--/v1/{fields["public_id"]}.png",
                    _ => $"https://res.cloudinary.com/test-cloud/image/authenticated/v1/{fields["public_id"]}.png"
                };
            }
            else
            {
                url = $"https://res.cloudinary.com/test-cloud/image/upload/v1/{fields["public_id"]}.png";
                if (mode == "wrong-host") url = url.Replace("res.cloudinary.com", "example.invalid");
                if (mode == "wrong-cloud") url = url.Replace("test-cloud", "another-cloud");
                if (mode == "query") url += "?redirect=other";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { public_id = mode == "wrong-id" ? "other" : fields["public_id"], secure_url = url, bytes = 123, sha256 = "untrusted" })) };
        }
    }
}
