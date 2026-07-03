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

    // Final product policy: JPG/JPEG + PNG only. WebP/GIF/BMP/TIFF are rejected at upload init
    // so we never accept a format that platform validation or the publisher would later block.
    // (Instagram PNG is converted to a JPEG derivative server-side at upload-complete.)
    private static readonly HashSet<string> _allowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png"
    };

    // Final product policy: MP4 + MOV only. MOV (video/quicktime) stays for iPhone compatibility.
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
        TimeSpan? defaultPublishingUrlExpiration = null)
    {
        _storage = storage;
        _storageOpts = storageOpts;
        _runMode = runMode;
        _logger = logger;
        _uploadUrlExpiration = uploadUrlExpiration;
        _maxImageFileSizeBytes = maxImageFileSizeBytes;
        _maxVideoFileSizeBytes = maxVideoFileSizeBytes;
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
        Guid userId,
        Guid workspaceId,
        Platform platform,
        Guid mediaId,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidMediaType(contentType))
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed types: {string.Join(", ", AllowedContentTypes)}");

        var providerPlatform = MapPlatformToProviderSegment(platform);

        var mediaType = GetMediaType(contentType);
        var extension = ExtensionFor(fileName, contentType, mediaType);
        var safeName = SanitizeFileName(fileName, extension);

        // User + workspace + platform scoped, server-chosen path. The leading
        // users/{userId} segment is the authenticated PostPilot app user id (never an
        // email, Meta account id, page id, or provider user id). The caller is
        // responsible for verifying that this user has access to the workspace before
        // we mint an upload URL. MVP assumption: each media upload belongs to one
        // platform only — no cross-posting yet, so the path can carry a single
        // deterministic platform segment.
        var key = $"users/{userId:D}/workspaces/{workspaceId:D}/providers/{providerPlatform}/media/{mediaId:D}/{safeName}";

        var uploadUrl = await _storage.CreateUploadUrlAsync(key, contentType, _uploadUrlExpiration, cancellationToken);

        _logger.LogInformation(
            "Generated user/workspace-scoped upload URL for {MediaType} mediaId={MediaId} user={UserId} workspace={WorkspaceId} platform={Platform} key={Key} (mode={RunMode})",
            mediaType, mediaId, userId, workspaceId, providerPlatform, key, _runMode);

        return new UploadUrlResult(uploadUrl, key, mediaType);
    }

    /// <summary>
    /// Maps a publishing <see cref="Platform"/> to the token used in the storage key.
    /// The mapping is deliberately a hand-rolled switch (not <c>ToString().ToLower()</c>)
    /// so adding a new enum member can't silently change the storage layout — every new
    /// platform that should be uploadable has to land here explicitly.
    /// </summary>
    internal static string MapPlatformToProviderSegment(Platform platform) => platform switch
    {
        Platform.Facebook  => "meta-facebook",
        Platform.Instagram => "meta-instagram",
        _ => throw new ArgumentException(
            $"Platform '{platform}' is not supported for media uploads yet. " +
            "Supported: Facebook, Instagram."),
    };

    public string GenerateDownloadUrl(string storageKey, TimeSpan expiration)
    {
        return _storage.CreateDownloadUrlAsync(storageKey, expiration).GetAwaiter().GetResult();
    }

    public async Task<string> GetPublishingUrlAsync(string storageKey, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var exp = expiration ?? _defaultPublishingUrlExpiration;

        // Publishing needs a short-lived, PRIVATE signed URL that Meta can fetch directly from
        // the object store. Only Supabase / S3-compatible backends can mint one. The old
        // anonymous GET /api/media/files/{storageKey} proxy route was removed in the media-privacy
        // redesign, so there is no proxy fallback any more: a backend that cannot sign a URL must
        // fail the publish clearly rather than hand Meta a dead URL that would 404.
        if (!_storageOpts.IsSupabase && !_storageOpts.IsS3Compatible)
        {
            throw new NotSupportedException(
                "Cannot produce a media publishing URL: the configured MediaStorage provider cannot " +
                "mint a signed download URL, and the legacy /api/media/files proxy route was removed. " +
                "Publishing requires MediaStorage__Provider=supabase or s3-compatible.");
        }

        // Freshly signed on every call, so a post scheduled far in the future still gets a valid
        // URL at publish time. A signing failure (e.g. a Supabase outage) propagates and fails the
        // publish attempt — we intentionally do NOT fall back to a dead proxy URL.
        return await _storage.CreateDownloadUrlAsync(storageKey, exp, cancellationToken);
    }

    public string GetPublishingUrl(string storageKey, TimeSpan? expiration = null)
    {
        // Sync wrapper for legacy callers — blocking on object-storage signing here.
        // Prefer GetPublishingUrlAsync from publish/worker code paths.
        return GetPublishingUrlAsync(storageKey, expiration).GetAwaiter().GetResult();
    }

    public bool IsStorageKey(string? mediaUrl)
    {
        if (string.IsNullOrEmpty(mediaUrl)) return false;
        if (mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;

        // Accept the legacy "media/{guid}.{ext}" shape, the workspace-scoped
        // "workspaces/{ws}/..." shape, and the new user-scoped
        // "users/{userId}/workspaces/{ws}/..." shape.
        return mediaUrl.StartsWith("media/", StringComparison.OrdinalIgnoreCase)
            || mediaUrl.StartsWith("workspaces/", StringComparison.OrdinalIgnoreCase)
            || mediaUrl.StartsWith("users/", StringComparison.OrdinalIgnoreCase);
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
