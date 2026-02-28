namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for media handling.
/// Bound from "Media" config section. All defaults in appsettings.common.json.
/// </summary>
public class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>
    /// Upload URL expiration in minutes.
    /// Can also be set via MEDIA_UPLOAD_URL_EXPIRATION_MINUTES env var (takes precedence).
    /// </summary>
    public int UploadUrlExpirationMinutes { get; set; }

    /// <summary>
    /// Maximum image file size in bytes.
    /// </summary>
    public long MaxImageFileSizeBytes { get; set; }

    /// <summary>
    /// Maximum video file size in bytes.
    /// </summary>
    public long MaxVideoFileSizeBytes { get; set; }

    /// <summary>
    /// Base URL used for local media serving (local mode only).
    /// Can also be overridden via PUBLIC_URL env var.
    /// </summary>
    public string LocalServerBaseUrl { get; set; } = null!;
}
