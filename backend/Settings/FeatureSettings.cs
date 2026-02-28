namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration settings for feature flags.
/// Bound from "Features" config section. All defaults in appsettings.common.json.
/// </summary>
public class FeatureSettings
{
    /// <summary>
    /// Whether to enable fetching engagement metrics from Facebook.
    /// </summary>
    public bool EnableEngagementFetch { get; set; }

    /// <summary>
    /// Whether to send custom thumbnails to Facebook when publishing videos.
    /// </summary>
    public bool EnableFacebookThumbnail { get; set; }
}
