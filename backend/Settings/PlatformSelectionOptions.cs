namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for platform selection behavior.
/// Bound from "Features:PlatformSelection" config section. All defaults in appsettings.common.json.
/// </summary>
public class PlatformSelectionOptions
{
    /// <summary>
    /// Maximum number of platforms that can be selected per post.
    /// </summary>
    public int MaxPlatformsPerPost { get; set; }
}
