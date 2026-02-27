using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Application-level media orchestration service.
/// Generates storage keys, validates content types, and delegates storage operations to IMediaStorageProvider.
/// </summary>
public interface IMediaService
{
    /// <summary>
    /// The current application run mode (local or server).
    /// </summary>
    AppRunMode RunMode { get; }

    /// <summary>
    /// Generates an upload URL and storage key for a file.
    /// </summary>
    /// <param name="fileName">Original file name (used to extract extension)</param>
    /// <param name="contentType">MIME type of the file</param>
    /// <returns>Upload URL result containing the URL and storage key</returns>
    Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType);

    /// <summary>
    /// Generates a download URL for a stored file.
    /// </summary>
    /// <param name="storageKey">The storage key (e.g., "media/guid.jpg")</param>
    /// <param name="expiration">How long the URL should be valid</param>
    /// <returns>Publicly accessible download URL</returns>
    string GenerateDownloadUrl(string storageKey, TimeSpan expiration);

    /// <summary>
    /// Determines if the given media URL is a storage key (vs an external URL).
    /// </summary>
    bool IsStorageKey(string? mediaUrl);

    /// <summary>
    /// Validates if the content type is an allowed image type.
    /// </summary>
    bool IsValidImageType(string contentType);

    /// <summary>
    /// Validates if the content type is an allowed video type.
    /// </summary>
    bool IsValidVideoType(string contentType);

    /// <summary>
    /// Validates if the content type is an allowed media type (image or video).
    /// </summary>
    bool IsValidMediaType(string contentType);

    /// <summary>
    /// Gets the MediaType enum value for a given content type.
    /// Returns None if the content type is not a valid media type.
    /// </summary>
    MediaType GetMediaType(string contentType);

    /// <summary>
    /// Gets the list of allowed image content types.
    /// </summary>
    IReadOnlyCollection<string> AllowedImageTypes { get; }

    /// <summary>
    /// Gets the list of allowed video content types.
    /// </summary>
    IReadOnlyCollection<string> AllowedVideoTypes { get; }

    /// <summary>
    /// Gets the list of all allowed content types (images and videos).
    /// </summary>
    IReadOnlyCollection<string> AllowedContentTypes { get; }

    /// <summary>
    /// Gets the maximum allowed image file size in bytes.
    /// </summary>
    long MaxImageFileSizeBytes { get; }

    /// <summary>
    /// Gets the maximum allowed video file size in bytes.
    /// </summary>
    long MaxVideoFileSizeBytes { get; }

    /// <summary>
    /// Gets the maximum allowed file size for a given content type.
    /// </summary>
    long GetMaxFileSizeBytes(string contentType);

    /// <summary>
    /// Gets a local file path for a given storage key.
    /// For local mode, returns the actual file path.
    /// For server mode, may not be available (stub throws).
    /// Returns null if the file cannot be accessed locally.
    /// </summary>
    Task<string?> GetLocalFilePathAsync(string storageKey);

    /// <summary>
    /// Gets the underlying storage provider implementation.
    /// </summary>
    IMediaStorageProvider StorageProvider { get; }

    /// <summary>
    /// Backward-compatible sync wrapper for GetLocalFilePathAsync.
    /// Prefer GetLocalFilePathAsync for new code.
    /// </summary>
    string? GetLocalFilePath(string storageKey);
}

/// <summary>
/// Result of generating an upload URL.
/// </summary>
public record UploadUrlResult(
    /// <summary>
    /// URL for uploading the file (pre-signed PUT URL or local endpoint).
    /// </summary>
    string UploadUrl,

    /// <summary>
    /// Storage key where the file will be stored (e.g., "media/guid.jpg").
    /// </summary>
    string StorageKey,

    /// <summary>
    /// The media type of the file being uploaded.
    /// </summary>
    MediaType MediaType
);
