using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Unified media service that delegates storage operations to IMediaStorageProvider.
/// Handles app-level concerns: key naming, content type validation, media type detection.
/// </summary>
public class MediaService : IMediaService
{
    private readonly IMediaStorageProvider _storage;
    private readonly AppRunMode _runMode;
    private readonly TimeSpan _uploadUrlExpiration;
    private readonly ILogger<MediaService> _logger;
    private readonly long _maxVideoFileSizeBytes;

    private static readonly HashSet<string> _allowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif"
    };

    private static readonly HashSet<string> _allowedVideoTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "video/mp4"
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
        AppRunMode runMode,
        ILogger<MediaService> logger,
        TimeSpan uploadUrlExpiration,
        long maxImageFileSizeBytes,
        long maxVideoFileSizeBytes)
    {
        _storage = storage;
        _runMode = runMode;
        _logger = logger;
        _uploadUrlExpiration = uploadUrlExpiration;
        _maxImageFileSizeBytes = maxImageFileSizeBytes;
        _maxVideoFileSizeBytes = maxVideoFileSizeBytes;
    }

    public async Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType)
    {
        if (!IsValidMediaType(contentType))
        {
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed types: {string.Join(", ", AllowedContentTypes)}");
        }

        var mediaType = GetMediaType(contentType);
        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();

        if (string.IsNullOrEmpty(extension))
        {
            extension = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                "video/mp4" => ".mp4",
                _ => mediaType == MediaType.Video ? ".mp4" : ".jpg"
            };
        }

        var key = $"media/{Guid.NewGuid()}{extension}";
        var uploadUrl = await _storage.CreateUploadUrlAsync(key, contentType, _uploadUrlExpiration);

        _logger.LogInformation("Generated upload URL for {MediaType} key {Key} (mode={RunMode})",
            mediaType, key, _runMode);

        return new UploadUrlResult(uploadUrl, key, mediaType);
    }

    public string GenerateDownloadUrl(string storageKey, TimeSpan expiration)
    {
        return _storage.CreateDownloadUrlAsync(storageKey, expiration).GetAwaiter().GetResult();
    }

    public bool IsStorageKey(string? mediaUrl)
    {
        return !string.IsNullOrEmpty(mediaUrl) &&
               mediaUrl.StartsWith("media/", StringComparison.OrdinalIgnoreCase) &&
               !mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
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
}
