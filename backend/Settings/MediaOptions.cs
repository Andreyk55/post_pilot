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

    /// <summary>
    /// Public-facing URL override (e.g., ngrok tunnel URL).
    /// When set, used instead of <see cref="LocalServerBaseUrl"/> for generating
    /// download/upload URLs that external services (Meta, AI) must reach.
    /// Centralized in <see cref="AppOptions.PublicUrl"/> and propagated here via PostConfigure.
    /// Can also be set directly via config key Media:PublicUrl or env var Media__PublicUrl.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>
    /// Returns <see cref="PublicUrl"/> if set, otherwise <see cref="LocalServerBaseUrl"/>.
    /// </summary>
    public string EffectiveBaseUrl => !string.IsNullOrWhiteSpace(PublicUrl) ? PublicUrl : LocalServerBaseUrl;
}
