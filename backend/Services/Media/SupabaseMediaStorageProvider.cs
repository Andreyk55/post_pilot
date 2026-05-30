using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Storage provider backed by Supabase Storage (PostgREST + Storage REST).
///
/// <para>
/// All requests use the Supabase service-role key, so the bucket can stay private
/// and we never expose the key to the browser. The browser only ever sees short-lived
/// signed URLs that this provider mints.
/// </para>
///
/// <para>
/// Key flows:
///   - Upload: <see cref="CreateUploadUrlAsync"/> calls POST /storage/v1/object/upload/sign,
///     receives a token, and builds the signed PUT URL the browser uploads to directly.
///   - Download: <see cref="CreateDownloadUrlAsync"/> calls POST /storage/v1/object/sign,
///     receives a path with a signed token, and returns the fully qualified URL.
///   - Read (internal): <see cref="OpenReadAsync"/> uses the service-role key directly to
///     stream bytes — used by AI/validation flows where exposing a signed URL is overkill.
/// </para>
/// </summary>
public class SupabaseMediaStorageProvider : IMediaStorageProvider, IDisposable
{
    // The Supabase Storage REST API. Paths are relative to {ProjectUrl}/storage/v1.
    //   POST /object/upload/sign/{bucket}/{*path}     → { url, token }
    //   POST /object/sign/{bucket}/{*path}            → { signedURL }
    //   GET  /object/{bucket}/{*path}                 → file bytes
    //   HEAD /object/info/{bucket}/{*path}            → object metadata (size, type)
    //   DELETE /object/{bucket}/{*path}               → delete
    private const string ApiPrefix = "/storage/v1";

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _bucket;
    private readonly SupabaseStorageOptions _options;
    private readonly ILogger<SupabaseMediaStorageProvider> _logger;
    private bool _disposed;

    public SupabaseMediaStorageProvider(
        MediaStorageOptions storageOptions,
        ILogger<SupabaseMediaStorageProvider> logger)
        : this(storageOptions, logger, http: null)
    {
    }

    /// <summary>
    /// Testing seam — accepts a pre-configured <see cref="HttpClient"/>. In normal DI
    /// this is null and the provider creates its own.
    /// </summary>
    internal SupabaseMediaStorageProvider(
        MediaStorageOptions storageOptions,
        ILogger<SupabaseMediaStorageProvider> logger,
        HttpClient? http)
    {
        _options = storageOptions.Supabase;
        _bucket = _options.Bucket;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.Url))
            throw new InvalidOperationException("MediaStorage:Supabase:Url is required for SupabaseMediaStorageProvider.");
        if (string.IsNullOrWhiteSpace(_options.ServiceRoleKey))
            throw new InvalidOperationException("MediaStorage:Supabase:ServiceRoleKey is required for SupabaseMediaStorageProvider.");
        if (string.IsNullOrWhiteSpace(_bucket))
            throw new InvalidOperationException("MediaStorage:Supabase:Bucket is required for SupabaseMediaStorageProvider.");

        _baseUri = new Uri(_options.Url.TrimEnd('/') + "/");

        _http = http ?? new HttpClient
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };

        // Supabase requires BOTH headers on every authenticated request:
        //   apikey: <service role>  — selects the project
        //   Authorization: Bearer <service role>  — authenticates as service role
        if (_http.DefaultRequestHeaders.Authorization is null)
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);
        }
        if (!_http.DefaultRequestHeaders.Contains("apikey"))
        {
            _http.DefaultRequestHeaders.Add("apikey", _options.ServiceRoleKey);
        }

        _logger.LogInformation(
            "SupabaseMediaStorageProvider initialized. Project={Url} Bucket={Bucket} SignedUrlExpirySeconds={Expiry}",
            _baseUri, _bucket, _options.SignedUrlExpirySeconds);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private static string EncodePath(string storageKey)
    {
        // Encode each path segment individually so '/' separators survive.
        return string.Join('/', storageKey.Split('/').Select(Uri.EscapeDataString));
    }

    private Uri BuildUri(string relativePath) =>
        new(_baseUri, relativePath.TrimStart('/'));

    // ── IMediaStorageProvider ────────────────────────────────────────────────

    public async Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        // POST /storage/v1/object/upload/sign/{bucket}/{path}
        //   body: { "expiresIn": <seconds> }   (optional — defaults to ~2h)
        //   resp: { "url": "/object/upload/sign/...", "token": "..." }
        //
        // The returned `url` is RELATIVE to the storage base. We expand it and append
        // ?token=<token> so the browser can PUT directly without re-signing.
        var path = $"{ApiPrefix}/object/upload/sign/{Uri.EscapeDataString(_bucket)}/{EncodePath(storageKey)}";

        var resp = await _http.PostAsJsonAsync(path, new
        {
            expiresIn = (int)expires.TotalSeconds,
        }, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Supabase upload-sign failed for key {Key}: status={Status} body={Body}",
                storageKey, (int)resp.StatusCode, body);
            throw new InvalidOperationException(
                $"Supabase failed to issue an upload URL for '{storageKey}' (HTTP {(int)resp.StatusCode}).");
        }

        var signed = await resp.Content.ReadFromJsonAsync<SupabaseSignedUploadResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Supabase returned an empty upload-sign response.");

        var url = BuildSignedUploadUrl(signed);
        _logger.LogInformation(
            "Generated Supabase upload URL for key {Key} (expires in {Expires})",
            storageKey, expires);
        return url;
    }

    private string BuildSignedUploadUrl(SupabaseSignedUploadResponse signed)
    {
        // Supabase returns a relative URL like "/object/upload/sign/bucket/key?token=..."
        // or just "/object/upload/sign/bucket/key" + a separate token. Handle both.
        var url = signed.Url ?? string.Empty;
        var token = signed.Token ?? string.Empty;

        var resolved = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(url)
            : BuildUri(ApiPrefix + (url.StartsWith("/") ? url : "/" + url));

        if (!string.IsNullOrEmpty(token) && !resolved.Query.Contains("token=", StringComparison.OrdinalIgnoreCase))
        {
            var sep = resolved.Query.Length > 0 ? "&" : "?";
            return resolved.ToString() + sep + "token=" + Uri.EscapeDataString(token);
        }
        return resolved.ToString();
    }

    public async Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        // POST /storage/v1/object/sign/{bucket}/{path}
        //   body: { "expiresIn": <seconds> }
        //   resp: { "signedURL": "/object/sign/..." }   (note: capital URL)
        var path = $"{ApiPrefix}/object/sign/{Uri.EscapeDataString(_bucket)}/{EncodePath(storageKey)}";

        var resp = await _http.PostAsJsonAsync(path, new
        {
            expiresIn = (int)expires.TotalSeconds,
        }, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Supabase download-sign failed for key {Key}: status={Status} body={Body}",
                storageKey, (int)resp.StatusCode, body);
            throw new InvalidOperationException(
                $"Supabase failed to issue a download URL for '{storageKey}' (HTTP {(int)resp.StatusCode}).");
        }

        var signed = await resp.Content.ReadFromJsonAsync<SupabaseSignedDownloadResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Supabase returned an empty download-sign response.");

        var rel = signed.SignedUrl ?? throw new InvalidOperationException(
            "Supabase signed-download response missing signedURL field.");

        var url = rel.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? rel
            : BuildUri(ApiPrefix + (rel.StartsWith("/") ? rel : "/" + rel)).ToString();

        return url;
    }

    public async Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // GET /storage/v1/object/{bucket}/{path}
        // Authenticated with the service-role key so we don't need a signed URL.
        var path = $"{ApiPrefix}/object/{Uri.EscapeDataString(_bucket)}/{EncodePath(storageKey)}";
        var resp = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();

        return await resp.Content.ReadAsStreamAsync(cancellationToken);
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // DELETE /storage/v1/object/{bucket}/{path}
        var path = $"{ApiPrefix}/object/{Uri.EscapeDataString(_bucket)}/{EncodePath(storageKey)}";
        var resp = await _http.DeleteAsync(path, cancellationToken);

        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Supabase delete: object {Key} already gone.", storageKey);
            return;
        }
        resp.EnsureSuccessStatusCode();
        _logger.LogInformation("Deleted Supabase object {Key} from bucket {Bucket}", storageKey, _bucket);
    }

    public async Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // Object storage has no real local file. Materialize one in the system temp dir
        // so callers that require a path (ffprobe, ImageSharp) can work. The
        // "postpilot-media-" prefix lets MediaService.TryCleanupTempLocalPath recognize
        // and delete it after use.
        await using var source = await OpenReadAsync(storageKey, cancellationToken);
        if (source == null) return null;

        var extension = Path.GetExtension(storageKey);
        var tempPath = Path.Combine(Path.GetTempPath(), $"postpilot-media-{Guid.NewGuid()}{extension}");
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }
        return tempPath;
    }

    public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        // Supabase uploads go through the signed-URL flow only. Direct server-side
        // upload through this provider is intentionally unsupported so we never
        // double up on bandwidth (browser → API → Supabase). If a future caller
        // really needs a server-side put it should sign-then-PUT explicitly.
        throw new InvalidOperationException(
            "Direct save is not supported by SupabaseMediaStorageProvider. " +
            "Use the signed upload URL from /api/media/uploads/init instead.");
    }

    [Obsolete("Use ObjectExistsAsync.")]
    public bool Exists(string storageKey)
    {
        return ObjectExistsAsync(storageKey).GetAwaiter().GetResult();
    }

    public async Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var info = await GetObjectInfoAsync(storageKey, cancellationToken);
        return info is not null;
    }

    public async Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        // Supabase exposes /object/info/{bucket}/{path} for metadata. We prefer this
        // over HEAD because Supabase's HEAD on /object/... has historically returned
        // inconsistent Content-Length values across CDN tiers.
        var path = $"{ApiPrefix}/object/info/{Uri.EscapeDataString(_bucket)}/{EncodePath(storageKey)}";

        var resp = await _http.GetAsync(path, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!resp.IsSuccessStatusCode)
        {
            // Fall back to HEAD on the object route — older Supabase deployments may
            // not implement /object/info. This keeps the provider resilient across
            // self-hosted Supabase versions.
            return await GetObjectInfoViaHeadAsync(storageKey, cancellationToken);
        }

        var info = await resp.Content.ReadFromJsonAsync<SupabaseObjectInfo>(cancellationToken);
        if (info is null) return null;

        return new StoredObjectInfo(
            SizeBytes: info.Size ?? 0,
            ContentType: info.ContentType,
            ETag: info.ETag,
            LastModified: info.LastModified);
    }

    private async Task<StoredObjectInfo?> GetObjectInfoViaHeadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = $"{ApiPrefix}/object/{Uri.EscapeDataString(_bucket)}/{EncodePath(storageKey)}";
        using var req = new HttpRequestMessage(HttpMethod.Head, path);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();

        return new StoredObjectInfo(
            SizeBytes: resp.Content.Headers.ContentLength ?? 0,
            ContentType: resp.Content.Headers.ContentType?.MediaType,
            ETag: resp.Headers.ETag?.Tag,
            LastModified: resp.Content.Headers.LastModified?.UtcDateTime);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _http.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    // ── Wire types ───────────────────────────────────────────────────────────
    private sealed class SupabaseSignedUploadResponse
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
    }

    private sealed class SupabaseSignedDownloadResponse
    {
        // Supabase returns the field as "signedURL" (capital URL).
        [JsonPropertyName("signedURL")] public string? SignedUrl { get; set; }
    }

    private sealed class SupabaseObjectInfo
    {
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("contentType")] public string? ContentType { get; set; }
        [JsonPropertyName("etag")] public string? ETag { get; set; }
        [JsonPropertyName("lastModified")] public DateTime? LastModified { get; set; }
    }
}
