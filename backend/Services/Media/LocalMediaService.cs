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

    private static readonly HashSet<string> _allowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/gif"
    };

    private const long _maxFileSizeBytes = 10 * 1024 * 1024; // 10MB

    public IReadOnlyCollection<string> AllowedContentTypes => _allowedContentTypes;
    public long MaxFileSizeBytes => _maxFileSizeBytes;

    public LocalMediaService(ILogger<LocalMediaService> logger, string? baseUrl = null)
    {
        _uploadPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads");

        // Check for PUBLIC_URL env var (for ngrok or other tunneling)
        // This URL is what Meta will use to fetch images
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
        if (!IsValidImageType(contentType))
        {
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed types: {string.Join(", ", _allowedContentTypes)}");
        }

        var extension = Path.GetExtension(fileName)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = contentType switch
            {
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/png" => ".png",
                "image/gif" => ".gif",
                _ => ".jpg"
            };
        }

        // For local dev, use a flat filename (no subdirectory) to avoid URL encoding issues
        var fileId = $"{Guid.NewGuid()}{extension}";
        var s3Key = $"media/{fileId}";

        // For local dev, the upload URL points to our own API endpoint
        // Use just the fileId to avoid path encoding issues
        var uploadUrl = $"{_baseUrl}/api/media/upload/{fileId}";

        _logger.LogInformation("Generated local upload URL for key {S3Key}", s3Key);

        return Task.FromResult(new UploadUrlResult(uploadUrl, s3Key));
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
        return _allowedContentTypes.Contains(contentType);
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
