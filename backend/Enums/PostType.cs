namespace PostPilot.Api.Enums;

/// <summary>
/// Type of post content (feed post vs story).
/// </summary>
public enum PostType
{
    /// <summary>Standard feed post (default).</summary>
    Feed = 0,

    /// <summary>Story content (24-hour ephemeral).</summary>
    Story = 1
}
