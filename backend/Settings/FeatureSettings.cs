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
}
