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
    /// Storage key uses the legacy <c>media/{guid}.{ext}</c> shape. Prefer
    /// <see cref="GenerateUploadUrlAsync(Guid, Guid, string, string, CancellationToken)"/>
    /// for new code so the key is workspace-scoped on the backend side.
    /// </summary>
    /// <param name="fileName">Original file name (used to extract extension)</param>
    /// <param name="contentType">MIME type of the file</param>
    /// <returns>Upload URL result containing the URL and storage key</returns>
    Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType);

    /// <summary>
    /// Workspace + platform scoped variant. Generates a storage key that embeds the
    /// workspace, the target publishing platform, and the media id, so storage layout
    /// alone enforces tenant separation and per-platform partitioning. Frontend never
    /// picks the key — it provides the original file name and content type plus the
    /// selected <see cref="Platform"/>; the backend composes the final path.
    ///
    /// <para>Final key shape:
    /// <c>workspaces/{workspaceId}/providers/{providerPlatform}/media/{mediaId}/{safeFileName}</c>
    /// </para>
    ///
    /// <para>
    /// Platform → providerPlatform token mapping (server-side, hard-coded):
    ///   <see cref="Platform.Facebook"/>  → <c>meta-facebook</c>
    ///   <see cref="Platform.Instagram"/> → <c>meta-instagram</c>
    /// Any other platform value is rejected with <see cref="ArgumentException"/>.
    /// </para>
    /// </summary>
    Task<UploadUrlResult> GenerateUploadUrlAsync(
        Guid workspaceId,
        Platform platform,
        Guid mediaId,
        string fileName,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a download URL for a stored file.
    /// </summary>
    /// <param name="storageKey">The storage key (e.g., "media/guid.jpg")</param>
    /// <param name="expiration">How long the URL should be valid</param>
    /// <returns>Publicly accessible download URL</returns>
    string GenerateDownloadUrl(string storageKey, TimeSpan expiration);

    /// <summary>
    /// Returns the URL external services (e.g. Meta Graph API) should fetch the media from.
    ///
    /// <para>
    /// For providers that can mint short-lived signed download URLs (Supabase, S3-compatible
    /// in server mode), this returns a freshly-signed URL pointing directly at the bucket —
    /// the worker calls this at publish time, so a post scheduled 30 days in the future still
    /// hands Meta a valid URL the moment publishing runs.
    /// </para>
    ///
    /// <para>
    /// For local-disk (development) this falls back to the API's
    /// <c>/api/media/files/{storageKey}</c> route rooted at <c>App.PublicUrl</c>, which the API
    /// streams from local storage.
    /// </para>
    /// </summary>
    Task<string> GetPublishingUrlAsync(string storageKey, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous convenience wrapper around <see cref="GetPublishingUrlAsync"/>. For object-storage
    /// backends this performs a blocking network call to Supabase/S3 to sign the URL — prefer the
    /// async variant in publish/worker code paths. Kept for legacy sync callers only.
    /// </summary>
    string GetPublishingUrl(string storageKey, TimeSpan? expiration = null);

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

    /// <summary>
    /// Deletes a path previously returned by GetLocalFilePathAsync if (and only if)
    /// it is a temp file the storage provider materialized. Safe no-op for real
    /// LocalDisk storage paths. Call from a finally block after using the path.
    /// </summary>
    void TryCleanupTempLocalPath(string? localPath);
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
