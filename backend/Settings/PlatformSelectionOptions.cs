namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for platform selection behavior.
/// </summary>
public class PlatformSelectionOptions
{
    /// <summary>
    /// Maximum number of platforms that can be selected per post.
    /// Default is 1 (single platform selection).
    /// </summary>
    public int MaxPlatformsPerPost { get; set; } = 1;
}
