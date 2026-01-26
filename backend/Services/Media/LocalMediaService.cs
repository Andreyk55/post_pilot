using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Local file system-based implementation of media service for development.
/// Stores files locally and serves them via the MediaController.
/// </summary>
public class LocalMediaService : IMediaService
{
    private readonly string _uploadPath;
    private readonly string _baseUrl;
    private readonly ILogger<LocalMediaService> _logger;
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

    private const long DefaultMaxImageFileSizeBytes = 10 * 1024 * 1024; // 10MB
    private const long DefaultMaxVideoFileSizeBytes = 200 * 1024 * 1024; // 200MB

    public IReadOnlyCollection<string> AllowedImageTypes => _allowedImageTypes;
    public IReadOnlyCollection<string> AllowedVideoTypes => _allowedVideoTypes;
    public IReadOnlyCollection<string> AllowedContentTypes => _allowedImageTypes.Concat(_allowedVideoTypes).ToArray();
    public long MaxImageFileSizeBytes => DefaultMaxImageFileSizeBytes;
    public long MaxVideoFileSizeBytes => _maxVideoFileSizeBytes;

    public LocalMediaService(ILogger<LocalMediaService> logger, string? baseUrl = null, long? maxVideoFileSizeBytes = null)
    {
        _uploadPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _maxVideoFileSizeBytes = maxVideoFileSizeBytes ?? DefaultMaxVideoFileSizeBytes;

        // Check for PUBLIC_URL env var (for ngrok or other tunneling)
        // This URL is what Meta will use to fetch media (images/videos)
        _baseUrl = Environment.GetEnvironmentVariable("PUBLIC_URL")
                   ?? baseUrl
                   ?? "http://localhost:5122";

        _logger = logger;

        // Ensure uploads directory exists
        Directory.CreateDirectory(_uploadPath);
        _logger.LogInformation("LocalMediaService initialized. Upload path: {UploadPath}, Base URL: {BaseUrl}",
            _uploadPath, _baseUrl);
    }

    public Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType)
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

        // For local dev, use a flat filename (no subdirectory) to avoid URL encoding issues
        var fileId = $"{Guid.NewGuid()}{extension}";
        var s3Key = $"media/{fileId}";

        // For local dev, the upload URL points to our own API endpoint
        // Use just the fileId to avoid path encoding issues
        var uploadUrl = $"{_baseUrl}/api/media/upload/{fileId}";

        _logger.LogInformation("Generated local upload URL for {MediaType} key {S3Key}", mediaType, s3Key);

        return Task.FromResult(new UploadUrlResult(uploadUrl, s3Key, mediaType));
    }

    public string GenerateDownloadUrl(string s3Key, TimeSpan expiration)
    {
        // Extract just the filename from the s3Key
        var fileName = s3Key.StartsWith("media/") ? s3Key[6..] : s3Key;

        // For local dev, serve files from our API endpoint
        var downloadUrl = $"{_baseUrl}/api/media/files/{fileName}";

        _logger.LogDebug("Generated local download URL for key {S3Key}", s3Key);

        return downloadUrl;
    }

    public bool IsS3Key(string? mediaUrl)
    {
        return !string.IsNullOrEmpty(mediaUrl) &&
               mediaUrl.StartsWith("media/", StringComparison.OrdinalIgnoreCase) &&
               !mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsValidImageType(string contentType)
    {
        return _allowedImageTypes.Contains(contentType);
    }

    public bool IsValidVideoType(string contentType)
    {
        return _allowedVideoTypes.Contains(contentType);
    }

    public bool IsValidMediaType(string contentType)
    {
        return IsValidImageType(contentType) || IsValidVideoType(contentType);
    }

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

    /// <summary>
    /// Gets the local file path for a given key (can be s3Key like "media/x.jpg" or just filename "x.jpg").
    /// </summary>
    public string GetLocalPath(string key)
    {
        // Handle both "media/guid.ext" and "guid.ext" formats
        var fileName = key.StartsWith("media/") ? key[6..] : key;
        return Path.Combine(_uploadPath, fileName);
    }

    /// <summary>
    /// Saves a file to local storage.
    /// </summary>
    public async Task SaveFileAsync(string s3Key, Stream content)
    {
        var localPath = GetLocalPath(s3Key);
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var fileStream = new FileStream(localPath, FileMode.Create);
        await content.CopyToAsync(fileStream);

        _logger.LogInformation("Saved file to local path: {LocalPath}", localPath);
    }

    /// <summary>
    /// Checks if a file exists in local storage.
    /// </summary>
    public bool FileExists(string s3Key)
    {
        var localPath = GetLocalPath(s3Key);
        return File.Exists(localPath);
    }

    /// <summary>
    /// Opens a file stream for reading.
    /// </summary>
    public Stream OpenRead(string s3Key)
    {
        var localPath = GetLocalPath(s3Key);
        return new FileStream(localPath, FileMode.Open, FileAccess.Read);
    }
}
