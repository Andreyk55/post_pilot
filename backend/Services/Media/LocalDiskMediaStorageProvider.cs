namespace PostPilot.Api.Services.Media;

/// <summary>
/// Local filesystem storage provider.
/// Stores files under uploads/ and returns backend API endpoint URLs.
/// Used in APP_RUN_MODE=local.
/// </summary>
public class LocalDiskMediaStorageProvider : IMediaStorageProvider
{
    private readonly string _uploadPath;
    private readonly string _baseUrl;
    private readonly ILogger<LocalDiskMediaStorageProvider> _logger;

    public LocalDiskMediaStorageProvider(ILogger<LocalDiskMediaStorageProvider> logger, string baseUrl)
    {
        _uploadPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _baseUrl = baseUrl;
        _logger = logger;

        Directory.CreateDirectory(_uploadPath);
        _logger.LogInformation("LocalDiskMediaStorageProvider initialized. Upload path: {UploadPath}, Base URL: {BaseUrl}",
            _uploadPath, _baseUrl);
    }

    public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        var fileName = ExtractFileName(storageKey);
        var uploadUrl = $"{_baseUrl}/api/media/upload/{fileName}";

        _logger.LogInformation("Generated local upload URL for key {Key}", storageKey);
        return Task.FromResult(uploadUrl);
    }

    public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        var fileName = ExtractFileName(storageKey);
        var downloadUrl = $"{_baseUrl}/api/media/files/{fileName}";

        _logger.LogDebug("Generated local download URL for key {Key}", storageKey);
        return Task.FromResult(downloadUrl);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var localPath = GetLocalPath(storageKey);
        if (!File.Exists(localPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var localPath = GetLocalPath(storageKey);
        if (File.Exists(localPath))
        {
            File.Delete(localPath);
            _logger.LogInformation("Deleted local file: {Path}", localPath);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = GetLocalPath(storageKey);
        return Task.FromResult<string?>(File.Exists(path) ? path : null);
    }

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        var localPath = GetLocalPath(storageKey);
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(localPath, FileMode.Create);
        await content.CopyToAsync(fileStream, cancellationToken);

        _logger.LogInformation("Saved file to local path: {LocalPath}", localPath);
    }

    public bool Exists(string storageKey)
    {
        return File.Exists(GetLocalPath(storageKey));
    }

    public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(GetLocalPath(storageKey)));
    }

    public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        var path = GetLocalPath(storageKey);
        if (!File.Exists(path))
            return Task.FromResult<StoredObjectInfo?>(null);

        var info = new FileInfo(path);
        var contentType = SniffContentType(Path.GetExtension(path));
        return Task.FromResult<StoredObjectInfo?>(new StoredObjectInfo(
            SizeBytes: info.Length,
            ContentType: contentType,
            ETag: null,
            LastModified: info.LastWriteTimeUtc));
    }

    private static string? SniffContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        _ => null
    };

    /// <summary>
    /// Gets the local filesystem path for a storage key.
    /// </summary>
    internal string GetLocalPath(string storageKey)
    {
        var fileName = ExtractFileName(storageKey);
        return Path.Combine(_uploadPath, fileName);
    }

    private static string ExtractFileName(string storageKey)
    {
        return storageKey.StartsWith("media/") ? storageKey[6..] : storageKey;
    }
}
