namespace PostPilot.Api.Services.Media;

/// <summary>
/// Low-level abstraction for byte storage operations.
/// Handles URLs for upload/download and raw file access.
/// Does NOT handle app-level concerns like key naming, DB persistence, or MediaItem creation.
/// Implementations: LocalDiskMediaStorageProvider (local mode), ServerMediaStorageProvider (server mode stub).
/// </summary>
public interface IMediaStorageProvider
{
    /// <summary>
    /// Creates a URL for uploading a file.
    /// In local mode, returns a backend PUT endpoint URL.
    /// In server mode, returns a pre-signed PUT URL from the storage provider.
    /// </summary>
    /// <param name="storageKey">Storage key (e.g., "media/guid.jpg")</param>
    /// <param name="contentType">MIME type of the file</param>
    /// <param name="expires">How long the upload URL should be valid</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>URL the client should PUT the file to</returns>
    Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a URL for downloading/viewing a file.
    /// In local mode, returns a backend GET endpoint URL.
    /// In server mode, returns a pre-signed GET URL from the storage provider.
    /// </summary>
    /// <param name="storageKey">Storage key (e.g., "media/guid.jpg")</param>
    /// <param name="expires">How long the download URL should be valid</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Publicly accessible download URL</returns>
    Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a read stream for the file at the given key.
    /// Used by AI analysis, metadata extraction, and validation.
    /// </summary>
    /// <param name="storageKey">Storage key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Readable stream, or null if the file doesn't exist</returns>
    Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the file at the given key (for future cleanup).
    /// </summary>
    /// <param name="storageKey">Storage key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a local file path for the given key, if available.
    /// For local storage, returns the actual file path.
    /// For server storage, may download to a temp location and return that path.
    /// Returns null if the file cannot be accessed locally.
    /// </summary>
    /// <param name="storageKey">Storage key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a file to storage (used in local mode for direct upload).
    /// </summary>
    /// <param name="storageKey">Storage key</param>
    /// <param name="content">File content stream</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a file exists at the given key.
    /// </summary>
    bool Exists(string storageKey);
}
