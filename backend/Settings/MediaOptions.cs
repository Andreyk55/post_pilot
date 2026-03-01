namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for media handling.
/// Bound from "Media" config section. All defaults in appsettings.common.json.
/// </summary>
public class MediaOptions
{
    public const string SectionName = "Media";

    public int UploadUrlExpirationMinutes { get; set; }
    public long MaxImageFileSizeBytes { get; set; }
    public long MaxVideoFileSizeBytes { get; set; }
    public string LocalServerBaseUrl { get; set; } = null!;

    /// <summary>
    /// Propagated from <see cref="AppOptions.PublicUrl"/> via PostConfigure.
    /// </summary>
    public string? PublicUrl { get; set; }

    public string EffectiveBaseUrl => !string.IsNullOrWhiteSpace(PublicUrl) ? PublicUrl : LocalServerBaseUrl;
}
