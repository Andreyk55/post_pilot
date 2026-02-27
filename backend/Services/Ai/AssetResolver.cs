using PostPilot.Api.Services.Media;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Resolves asset URLs to bytes for AI processing.
/// Works with both local file storage and generic storage providers.
/// </summary>
public class AssetResolver : IAssetResolver
{
    private readonly IMediaService _mediaService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<AssetResolver> _logger;

    private static readonly TimeSpan DownloadUrlExpiration = TimeSpan.FromMinutes(15);

    public AssetResolver(
        IMediaService mediaService,
        HttpClient httpClient,
        ILogger<AssetResolver> logger)
    {
        _mediaService = mediaService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<ResolvedAsset> ResolveAsync(string assetUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            throw new ArgumentException("Asset URL cannot be empty", nameof(assetUrl));
        }

        // Determine if this is a storage key or external URL
        if (_mediaService.IsStorageKey(assetUrl))
        {
            return await ResolveStorageAssetAsync(assetUrl, cancellationToken);
        }

        // External URL - fetch directly
        return await ResolveExternalUrlAsync(assetUrl, cancellationToken);
    }

    public string GetPublicUrl(string assetUrl)
    {
        if (string.IsNullOrWhiteSpace(assetUrl))
        {
            throw new ArgumentException("Asset URL cannot be empty", nameof(assetUrl));
        }

        if (_mediaService.IsStorageKey(assetUrl))
        {
            return _mediaService.GenerateDownloadUrl(assetUrl, DownloadUrlExpiration);
        }

        // Already a public URL
        return assetUrl;
    }

    private async Task<ResolvedAsset> ResolveStorageAssetAsync(string storageKey, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Resolving storage asset: {StorageKey}", storageKey);

        // Try to read directly from storage provider
        var stream = await _mediaService.StorageProvider.OpenReadAsync(storageKey);
        if (stream != null)
        {
            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken);
                var bytes = ms.ToArray();
                var mimeType = GetMimeTypeFromExtension(Path.GetExtension(storageKey));

                _logger.LogDebug("Resolved storage asset: {StorageKey}, Size: {Size} bytes", storageKey, bytes.Length);
                return new ResolvedAsset(bytes, mimeType);
            }
        }

        // Fallback: generate a download URL and fetch via HTTP
        var downloadUrl = _mediaService.GenerateDownloadUrl(storageKey, DownloadUrlExpiration);
        return await ResolveExternalUrlAsync(downloadUrl, cancellationToken);
    }

    private async Task<ResolvedAsset> ResolveExternalUrlAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Resolving external URL: {Url}", url);

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            // If MIME type couldn't be determined from response, try extension
            if (mimeType == "application/octet-stream")
            {
                var uri = new Uri(url);
                mimeType = GetMimeTypeFromExtension(Path.GetExtension(uri.LocalPath));
            }

            _logger.LogDebug("Resolved external URL: {Url}, Size: {Size} bytes, Type: {MimeType}",
                url, bytes.Length, mimeType);

            return new ResolvedAsset(bytes, mimeType);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch asset from URL: {Url}", url);
            throw new InvalidOperationException($"Failed to fetch asset: {ex.Message}", ex);
        }
    }

    private static string GetMimeTypeFromExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            _ => "application/octet-stream"
        };
    }
}
