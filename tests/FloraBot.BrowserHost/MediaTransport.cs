using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace FloraBot.BrowserHost;

// Test-only transport: every download returns the bytes actually uploaded under that ID.
internal sealed class MediaTransport : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, byte[]> images = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Post && uri.Host == "api.cloudinary.com" && uri.AbsolutePath == "/v1_1/browser-fixture/image/upload" && request.Content is MultipartFormDataContent form)
        {
            HttpContent Field(string name) => form.Single(x => x.Headers.ContentDisposition?.Name?.Trim('"') == name);
            var id = await Field("public_id").ReadAsStringAsync(ct);
            var type = form.Any(x => x.Headers.ContentDisposition?.Name?.Trim('"') == "type") ? await Field("type").ReadAsStringAsync(ct) : "upload";
            var file = Field("file");
            var extension = file.Headers.ContentType?.MediaType switch { "image/png" => "png", "image/jpeg" => "jpg", "image/webp" => "webp", _ => throw new InvalidOperationException("Unsupported fixture image") };
            if (!images.TryAdd(type + ":" + id, await file.ReadAsByteArrayAsync(ct))) return new(HttpStatusCode.Conflict);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { public_id = id, secure_url = $"https://res.cloudinary.com/browser-fixture/image/{type}/v1/{id}.{extension}" }) };
        }
        if (request.Method == HttpMethod.Get && uri.Host == "api.cloudinary.com" && uri.AbsolutePath == "/v1_1/browser-fixture/image/download")
        {
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (query["type"] == "authenticated" && images.TryGetValue("authenticated:" + query["public_id"], out var bytes))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        if (request.Method == HttpMethod.Get && uri.Host == "res.cloudinary.com" && uri.AbsolutePath.StartsWith("/browser-fixture/image/upload/v1/", StringComparison.Ordinal))
        {
            var id = uri.AbsolutePath["/browser-fixture/image/upload/v1/".Length..];
            if (images.TryGetValue("upload:" + id[..id.LastIndexOf('.')], out var bytes)) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        return new(HttpStatusCode.NotFound);
    }
}
