using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

/// <summary>
/// Side table that tracks the lifecycle of a media upload (init → uploaded → optionally deleted).
///
/// This is intentionally NOT referenced by Post or PostMediaItem: posts continue to store
/// the storage key directly in their MediaUrl column. The Media table exists so the
/// /uploads/init + /uploads/complete flow has somewhere to record what was uploaded and when,
/// and so we can attribute objects to original filenames / content types after the fact.
/// </summary>
public class Media
{
    public Guid Id { get; set; }

    /// <summary>
    /// Workspace this media belongs to. Set from current user's workspace at
    /// upload init; never accepted from the client.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>Storage backend that owns this object (e.g. "s3-compatible", "local-disk").</summary>
    public string StorageProvider { get; set; } = string.Empty;

    /// <summary>Bucket name. Empty string for local-disk.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Object key inside the bucket — e.g. "media/{guid}.jpg".</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Original filename supplied by the client. Used for downloads / audit.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>MIME type declared at /init (and verified against the HEAD response at /complete).</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Final size in bytes. Null until /complete fetches HEAD metadata.</summary>
    public long? SizeBytes { get; set; }

    public MediaUploadStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UploadedAt { get; set; }

    // ── Instagram-safe JPEG derivative (Phase 3) ────────────────────────────────
    // When a PNG image is uploaded, the upload-complete flow generates an
    // Instagram-safe JPEG derivative (Meta accepts JPEG only for Instagram) and
    // stores it alongside the original. The original is NEVER replaced — Facebook
    // and previews keep using it. Instagram validation/publishing use the derivative.
    // These columns are null for JPEG uploads, videos, and legacy rows.

    /// <summary>
    /// Storage key of the generated Instagram JPEG derivative (e.g. the same media
    /// folder as the original with the original base filename and a <c>.jpg</c> extension).
    /// Null when no derivative was generated (JPEG original, video, or legacy row).
    /// </summary>
    public string? InstagramImageStorageKey { get; set; }

    /// <summary>MIME type of the derivative. Always <c>image/jpeg</c> when present.</summary>
    public string? InstagramImageMimeType { get; set; }

    /// <summary>Size in bytes of the generated derivative.</summary>
    public long? InstagramImageSizeBytes { get; set; }

    /// <summary>Pixel width of the generated derivative (after any downscale).</summary>
    public int? InstagramImageWidth { get; set; }

    /// <summary>Pixel height of the generated derivative (after any downscale).</summary>
    public int? InstagramImageHeight { get; set; }

    /// <summary>UTC timestamp the derivative was generated.</summary>
    public DateTime? InstagramImageGeneratedAt { get; set; }
}
