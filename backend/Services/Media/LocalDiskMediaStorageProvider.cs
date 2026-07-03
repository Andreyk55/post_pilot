namespace PostPilot.Api.Services.Media;

/// <summary>
/// Local filesystem storage provider.
/// Stores files under uploads/ and returns backend API endpoint URLs.
/// Used in APP_RUN_MODE=local.
/// </summary>
public class LocalDiskMediaStorageProvider : IMediaStorageProvider
{
    private readonly string _uploadPath;
    // Canonicalized upload root with a trailing separator, used as the containment boundary so
    // a resolved path must start with it to be considered inside the upload directory.
    private readonly string _uploadRootWithSep;
    private readonly string _baseUrl;
    private readonly ILogger<LocalDiskMediaStorageProvider> _logger;

    public LocalDiskMediaStorageProvider(ILogger<LocalDiskMediaStorageProvider> logger, string baseUrl)
    {
        _uploadPath = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _uploadRootWithSep = Path.GetFullPath(_uploadPath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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
        // Unsafe keys resolve to "not found" (null) rather than throwing, so the anonymous
        // /api/media/files reader can't be used to probe or read outside the upload root.
        if (!TryResolveLocalPath(storageKey, out var localPath) || !File.Exists(localPath))
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(localPath, FileMode.Open, FileAccess.Read);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (TryResolveLocalPath(storageKey, out var localPath) && File.Exists(localPath))
        {
            File.Delete(localPath);
            _logger.LogInformation("Deleted local file: {Path}", localPath);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (!TryResolveLocalPath(storageKey, out var path) || !File.Exists(path))
            return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(path);
    }

    public async Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        // Writes reject unsafe keys hard (GetLocalPath throws) so nothing is ever written
        // outside the upload root.
        var localPath = GetLocalPath(storageKey);
        var directory = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(localPath, FileMode.Create);
        await content.CopyToAsync(fileStream, cancellationToken);

        _logger.LogInformation("Saved file to local path: {LocalPath}", localPath);
    }

    public async Task UploadObjectAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        // Local disk infers content type from the file extension on read, so the
        // explicit contentType is only advisory here. Reuse the same write path.
        await SaveAsync(storageKey, content, cancellationToken);
    }

    public bool Exists(string storageKey)
    {
        return TryResolveLocalPath(storageKey, out var path) && File.Exists(path);
    }

    public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TryResolveLocalPath(storageKey, out var path) && File.Exists(path));
    }

    public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (!TryResolveLocalPath(storageKey, out var path) || !File.Exists(path))
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
    /// Gets the local filesystem path for a storage key, THROWING if the key is unsafe
    /// (absolute/rooted, contains ".." traversal, or resolves outside the upload root).
    /// Used by write paths that must fail hard rather than silently no-op.
    /// </summary>
    internal string GetLocalPath(string storageKey)
    {
        if (!TryResolveLocalPath(storageKey, out var path))
            throw new ArgumentException(
                "Unsafe storage key rejected (path traversal or absolute path).", nameof(storageKey));
        return path;
    }

    /// <summary>
    /// Resolves a storage key to an absolute path INSIDE the upload root, or returns false when
    /// the key is unsafe: empty, rooted/absolute, or resolving outside the root via ".." segments.
    /// The canonicalized-path containment check (GetFullPath + root-prefix) is authoritative; the
    /// explicit rooted-path check just fails fast on the most common escape.
    /// </summary>
    internal bool TryResolveLocalPath(string storageKey, out string localPath)
    {
        localPath = string.Empty;

        var fileName = ExtractFileName(storageKey);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        // An absolute/rooted key would make Path.Combine return it verbatim (escaping the root).
        if (Path.IsPathRooted(fileName))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_uploadPath, fileName));
        }
        catch
        {
            // Malformed path (invalid chars, too long, etc.) — treat as unsafe.
            return false;
        }

        // Must be strictly inside the upload root. The trailing separator on the root prevents a
        // sibling like "/data/uploads-evil" from matching the root "/data/uploads".
        if (!fullPath.StartsWith(_uploadRootWithSep, StringComparison.OrdinalIgnoreCase))
            return false;

        localPath = fullPath;
        return true;
    }

    private static string ExtractFileName(string storageKey)
    {
        if (string.IsNullOrEmpty(storageKey))
            return storageKey;
        return storageKey.StartsWith("media/") ? storageKey[6..] : storageKey;
    }
}
