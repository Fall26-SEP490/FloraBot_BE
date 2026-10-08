using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloraBot.Api.Modules.Notify;

public sealed record VerifiedMedia(string Url, string PublicId, string MimeType, int SizeBytes, string Sha256);

public sealed class MediaUnavailableException : Exception
{
    public MediaUnavailableException() : base("Dịch vụ ảnh chưa sẵn sàng. Vui lòng thử lại sau.") { }
}

// The returned metadata describes the stored original bytes, never a client-supplied URL or digest.
public sealed class CloudinaryMedia(HttpClient client, IConfiguration configuration, TimeProvider clock)
{
    public const int MaximumBytes = 5 * 1024 * 1024;

    public async Task<VerifiedMedia> VerifyUrlAsync(string? value, CancellationToken ct)
    {
        var cloud = configuration["CLOUDINARY_CLOUD_NAME"];
        if (string.IsNullOrWhiteSpace(cloud) || !Regex.IsMatch(cloud, "^[a-zA-Z0-9_-]+$")) throw new MediaUnavailableException();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.Host != "res.cloudinary.com" || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !Regex.IsMatch(url.AbsolutePath, $"^/{Regex.Escape(cloud)}/image/upload/v[0-9]+/florabot/[a-zA-Z0-9_-]{{1,128}}\\.(png|jpg|jpeg|webp)$"))
            throw new ArgumentException("Chọn URL ảnh gốc trong kho FloraBot đã cấu hình.", nameof(value));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new MediaUnavailableException();
        var bytes = await ReadBoundedAsync(response.Content, MaximumBytes, deadline.Token);
        var mime = ImageType(bytes);
        var filename = url.Segments[^1];
        var publicId = "florabot/" + filename[..filename.LastIndexOf('.')];
        return new VerifiedMedia(url.AbsoluteUri, publicId, mime, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public async Task<VerifiedMedia> UploadAsync(byte[] bytes, CancellationToken ct, bool authenticated = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ct = deadline.Token;
        var mime = ImageType(bytes);
        var cloud = configuration["CLOUDINARY_CLOUD_NAME"];
        var key = configuration["CLOUDINARY_API_KEY"];
        var secret = configuration["CLOUDINARY_API_SECRET"];
        if (string.IsNullOrWhiteSpace(cloud) || !Regex.IsMatch(cloud, "^[a-zA-Z0-9_-]+$") ||
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret)) throw new MediaUnavailableException();

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var publicId = $"{(authenticated ? "florabot-evidence" : "florabot")}/{Guid.NewGuid():N}";
        var deliveryType = authenticated ? "authenticated" : "upload";
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"overwrite=false&public_id={publicId}&timestamp={timestamp}{(authenticated ? "&type=authenticated" : "")}{secret}")));
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(key), "api_key");
        form.Add(new StringContent(timestamp), "timestamp");
        form.Add(new StringContent(publicId), "public_id");
        form.Add(new StringContent("false"), "overwrite");
        form.Add(new StringContent(signature), "signature");
        if (authenticated) form.Add(new StringContent("authenticated"), "type");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", "image");
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudinary.com/v1_1/{cloud}/image/upload") { Content = form };
        using var response = await client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new MediaUnavailableException();
        var payload = await ReadBoundedAsync(response.Content, 64 * 1024, ct);
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("public_id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() != publicId ||
            !root.TryGetProperty("secure_url", out var urlValue) || urlValue.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlValue.GetString(), UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.Host != "res.cloudinary.com" || !url.IsDefaultPort ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !Regex.IsMatch(url.AbsolutePath, $"^/{Regex.Escape(cloud)}/image/{deliveryType}/v[0-9]+/{Regex.Escape(publicId)}\\.(png|jpg|jpeg|webp)$"))
            throw new MediaUnavailableException();

        // Read only the original asset at the fixed CDN host; redirects are disabled in DI.
        byte[] original;
        if (authenticated) original = await ReadPrivateAsync(url.AbsoluteUri, ct);
        else
        {
            using var stored = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!stored.IsSuccessStatusCode) throw new MediaUnavailableException();
            original = await ReadBoundedAsync(stored.Content, MaximumBytes, ct);
        }
        var hash = SHA256.HashData(bytes);
        if (original.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(original)))
            throw new MediaUnavailableException();
        return new VerifiedMedia(url.AbsoluteUri, publicId, mime, bytes.Length, Convert.ToHexStringLower(hash));
    }

    public async Task<byte[]> ReadPrivateAsync(string value, CancellationToken ct)
    {
        var cloud = configuration["CLOUDINARY_CLOUD_NAME"];
        var key = configuration["CLOUDINARY_API_KEY"];
        var secret = configuration["CLOUDINARY_API_SECRET"];
        if (string.IsNullOrWhiteSpace(cloud) || !Regex.IsMatch(cloud, "^[a-zA-Z0-9_-]+$") ||
            string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret)) throw new MediaUnavailableException();
        var match = Regex.Match(value, $"^https://res\\.cloudinary\\.com/{Regex.Escape(cloud)}/image/authenticated/v[0-9]+/(florabot-evidence/[a-f0-9]{{32}})\\.(png|jpg|jpeg|webp)$");
        if (!match.Success) throw new MediaUnavailableException();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["expires_at"] = (now + 60).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["format"] = match.Groups[2].Value,
            ["public_id"] = match.Groups[1].Value,
            ["timestamp"] = now.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["type"] = "authenticated"
        };
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("&", fields.Select(x => $"{x.Key}={x.Value}")) + secret)));
        fields["api_key"] = key; fields["signature"] = signature;
        var query = string.Join("&", fields.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync($"https://api.cloudinary.com/v1_1/{cloud}/image/download?{query}", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new MediaUnavailableException();
        return await ReadBoundedAsync(response.Content, MaximumBytes, deadline.Token);
    }

    internal static string ImageType(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new ArgumentException("Ảnh phải có dung lượng từ 1 byte đến 5 MB.", nameof(bytes));
        if (bytes.Length >= 24 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 && bytes[^2] == 255 && bytes[^1] == 217) return "image/jpeg";
        if (bytes.Length >= 16 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw new ArgumentException("Chọn ảnh PNG, JPEG hoặc WebP.", nameof(bytes));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new MediaUnavailableException();
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit) throw new MediaUnavailableException();
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
