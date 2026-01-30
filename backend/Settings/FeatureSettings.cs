namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration settings for feature flags.
/// </summary>
public class FeatureSettings
{
    /// <summary>
    /// Whether to enable fetching engagement metrics from Facebook.
    /// Default is false (disabled) - will be enabled in a future release.
    /// </summary>
    public bool EnableEngagementFetch { get; set; } = false;

    /// <summary>
    /// Whether to send custom thumbnails to Facebook when publishing videos.
    /// Default is false (disabled) - thumbnails are only used in the app UI.
    /// </summary>
    public bool EnableFacebookThumbnail { get; set; } = false;
}
