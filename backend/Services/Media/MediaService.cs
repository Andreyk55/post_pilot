using System.Text.RegularExpressions;
using PostPilot.Api.Enums;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Unified media service that delegates storage operations to IMediaStorageProvider.
/// Handles app-level concerns: key naming, content type validation, media type detection.
/// </summary>
public class MediaService : IMediaService
{
    private readonly IMediaStorageProvider _storage;
    private readonly MediaStorageOptions _storageOpts;
    private readonly AppRunMode _runMode;
    private readonly TimeSpan _uploadUrlExpiration;
    private readonly TimeSpan _defaultPublishingUrlExpiration;
    private readonly ILogger<MediaService> _logger;
    private readonly long _maxVideoFileSizeBytes;
    private readonly string _publishingBaseUrl;

    private static readonly HashSet<string> _allowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp",
        "image/gif"
    };

    private static readonly HashSet<string> _allowedVideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4",
        "video/quicktime"
    };

    private readonly long _maxImageFileSizeBytes;

    public AppRunMode RunMode => _runMode;
    public IMediaStorageProvider StorageProvider => _storage;
    public IReadOnlyCollection<string> AllowedImageTypes => _allowedImageTypes;
    public IReadOnlyCollection<string> AllowedVideoTypes => _allowedVideoTypes;
    public IReadOnlyCollection<string> AllowedContentTypes => _allowedImageTypes.Concat(_allowedVideoTypes).ToArray();
    public long MaxImageFileSizeBytes => _maxImageFileSizeBytes;
    public long MaxVideoFileSizeBytes => _maxVideoFileSizeBytes;

    public MediaService(
        IMediaStorageProvider storage,
        MediaStorageOptions storageOpts,
        AppRunMode runMode,
        ILogger<MediaService> logger,
        TimeSpan uploadUrlExpiration,
        long maxImageFileSizeBytes,
        long maxVideoFileSizeBytes,
        string publishingBaseUrl,
        TimeSpan? defaultPublishingUrlExpiration = null)
    {
        _storage = storage;
        _storageOpts = storageOpts;
        _runMode = runMode;
        _logger = logger;
        _uploadUrlExpiration = uploadUrlExpiration;
        _maxImageFileSizeBytes = maxImageFileSizeBytes;
        _maxVideoFileSizeBytes = maxVideoFileSizeBytes;
        _publishingBaseUrl = publishingBaseUrl.TrimEnd('/');
        _defaultPublishingUrlExpiration = defaultPublishingUrlExpiration ?? TimeSpan.FromHours(1);
    }

    public async Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType)
    {
        if (!IsValidMediaType(contentType))
        {
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed types: {string.Join(", ", AllowedContentTypes)}");
        }

        var mediaType = GetMediaType(contentType);
        var extension = ExtensionFor(fileName, contentType, mediaType);
        var key = $"media/{Guid.NewGuid()}{extension}";
        var uploadUrl = await _storage.CreateUploadUrlAsync(key, contentType, _uploadUrlExpiration);

        _logger.LogInformation("Generated upload URL for {MediaType} key {Key} (mode={RunMode})",
            mediaType, key, _runMode);

        return new UploadUrlResult(uploadUrl, key, mediaType);
    }

    public async Task<UploadUrlResult> GenerateUploadUrlAsync(
        Guid workspaceId,
        string provider,
        string providerConnectionId,
        Guid mediaId,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidMediaType(contentType))
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed types: {string.Join(", ", AllowedContentTypes)}");

        var mediaType = GetMediaType(contentType);
        var extension = ExtensionFor(fileName, contentType, mediaType);
        var safeName = SanitizeFileName(fileName, extension);

        // provider / providerConnectionId are sanitized through the same allow-list as
        // file names so untrusted values can never escape their path segment. The
        // literal tokens "unassigned" / "none" stay intact because they only contain
        // [a-z0-9].
        var providerSegment = SanitizeSegment(provider, fallback: "unassigned");
        var connectionSegment = SanitizeSegment(providerConnectionId, fallback: "none");

        // Workspace + provider/account scoped, server-chosen path. The frontend never
        // picks any segment — it only supplies fileName/contentType (and optionally
        // selects a provider connection by id, which the upload service validates against
        // the current workspace before passing it in here). The legacy
        // "media/{guid}.{ext}" shape is kept on the OLDER overload so existing posts
        // referencing those keys keep resolving.
        var key = $"workspaces/{workspaceId:D}/providers/{providerSegment}/connections/{connectionSegment}/media/{mediaId:D}/{safeName}";

        var uploadUrl = await _storage.CreateUploadUrlAsync(key, contentType, _uploadUrlExpiration, cancellationToken);

        _logger.LogInformation(
            "Generated workspace-scoped upload URL for {MediaType} mediaId={MediaId} workspace={WorkspaceId} provider={Provider} connection={Connection} key={Key} (mode={RunMode})",
            mediaType, mediaId, workspaceId, providerSegment, connectionSegment, key, _runMode);

        return new UploadUrlResult(uploadUrl, key, mediaType);
    }

    public string GenerateDownloadUrl(string storageKey, TimeSpan expiration)
    {
        return _storage.CreateDownloadUrlAsync(storageKey, expiration).GetAwaiter().GetResult();
    }

    public async Task<string> GetPublishingUrlAsync(string storageKey, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var exp = expiration ?? _defaultPublishingUrlExpiration;

        // For object-storage backends, hand Meta a short-lived signed URL pointing
        // directly at the bucket. This is regenerated every time the worker publishes,
        // so a 30-day-future scheduled post still gets a fresh URL at publish time.
        // local-disk falls back to the API-proxied route because there's no bucket to
        // sign against.
        if (_storageOpts.IsSupabase || _storageOpts.IsS3Compatible)
        {
            try
            {
                return await _storage.CreateDownloadUrlAsync(storageKey, exp, cancellationToken);
            }
            catch (Exception ex)
            {
                // Fall back to the API-proxy route if signing failed for any reason
                // (e.g. Supabase outage). The proxy reads via the service-role key and
                // streams bytes to Meta, so publishing still works while we get paged.
                _logger.LogWarning(ex,
                    "Failed to mint signed publishing URL for {Key}; falling back to API proxy.", storageKey);
            }
        }

        return BuildProxiedPublishingUrl(storageKey);
    }

    public string GetPublishingUrl(string storageKey, TimeSpan? expiration = null)
    {
        // Sync wrapper for legacy callers — blocking on object-storage signing here.
        // Prefer GetPublishingUrlAsync from publish/worker code paths.
        return GetPublishingUrlAsync(storageKey, expiration).GetAwaiter().GetResult();
    }

    private string BuildProxiedPublishingUrl(string storageKey)
    {
        // The full storageKey (including any prefix like "media/" or "workspaces/.../")
        // is preserved in the path — MediaController.GetFile uses a catch-all route.
        var encoded = string.Join('/', storageKey.Split('/').Select(Uri.EscapeDataString));
        return $"{_publishingBaseUrl}/api/media/files/{encoded}";
    }

    public bool IsStorageKey(string? mediaUrl)
    {
        if (string.IsNullOrEmpty(mediaUrl)) return false;
        if (mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;

        // Accept both the legacy "media/{guid}.{ext}" shape and the new
        // "workspaces/{ws}/media/{id}/{name}" shape.
        return mediaUrl.StartsWith("media/", StringComparison.OrdinalIgnoreCase)
            || mediaUrl.StartsWith("workspaces/", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsValidImageType(string contentType) => _allowedImageTypes.Contains(contentType);
    public bool IsValidVideoType(string contentType) => _allowedVideoTypes.Contains(contentType);
    public bool IsValidMediaType(string contentType) => IsValidImageType(contentType) || IsValidVideoType(contentType);

    public MediaType GetMediaType(string contentType)
    {
        if (IsValidImageType(contentType))
            return MediaType.Image;
        if (IsValidVideoType(contentType))
            return MediaType.Video;
        return MediaType.None;
    }

    public long GetMaxFileSizeBytes(string contentType)
    {
        if (IsValidVideoType(contentType))
            return MaxVideoFileSizeBytes;
        return MaxImageFileSizeBytes;
    }

    public async Task<string?> GetLocalFilePathAsync(string storageKey)
    {
        return await _storage.GetLocalFilePathAsync(storageKey);
    }

    public string? GetLocalFilePath(string storageKey)
    {
        // Sync wrapper for backward compatibility
        return _storage.GetLocalFilePathAsync(storageKey).GetAwaiter().GetResult();
    }

    public void TryCleanupTempLocalPath(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath)) return;

        // Only delete files providers materialized into the system temp dir.
        // The prefix + temp-root check is what keeps us from ever deleting a real
        // LocalDisk storage file.
        var fileName = Path.GetFileName(localPath);
        if (!fileName.StartsWith("postpilot-media-", StringComparison.Ordinal)) return;

        var tempRoot = Path.GetTempPath();
        if (!localPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            File.Delete(localPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temp media file {Path}", localPath);
        }
    }

    private static string ExtensionFor(string fileName, string contentType, MediaType mediaType)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        if (!string.IsNullOrEmpty(ext))
            return ext;

        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            _ => mediaType == MediaType.Video ? ".mp4" : ".jpg",
        };
    }

    /// <summary>
    /// Sanitizes an arbitrary string for use as a single path segment. Collapses every
    /// run of disallowed characters to <c>-</c>, lowercases, trims leading/trailing
    /// dashes, and caps at 80 chars. Returns <paramref name="fallback"/> on empty input.
    /// Used for the provider / providerConnectionId path segments to keep the key shape
    /// predictable regardless of what value the caller hands in.
    /// </summary>
    private static string SanitizeSegment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var s = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (string.IsNullOrEmpty(s))
            return fallback;

        if (s.Length > 80)
            s = s[..80];

        return s;
    }

    /// <summary>
    /// Sanitizes a frontend-supplied file name so it is safe to use as a storage path
    /// segment: strips any directory components and reduces the basename to a small
    /// allow-listed character set. Always preserves <paramref name="extension"/>.
    /// </summary>
    private static string SanitizeFileName(string fileName, string extension)
    {
        // Strip any directory portion the client might have tried to slip in.
        var baseName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);

        // Collapse to [a-z0-9_-] + lowercase. Anything else (spaces, /, .., unicode) becomes '-'.
        baseName = Regex.Replace(baseName.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');

        if (string.IsNullOrEmpty(baseName))
            baseName = "file";

        if (baseName.Length > 80)
            baseName = baseName[..80];

        return baseName + extension;
    }
}
