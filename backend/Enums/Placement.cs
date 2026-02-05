namespace PostPilot.Api.Enums;

/// <summary>
/// Content placement within a platform (e.g., Feed, Story, Reel).
/// Used for platform-specific media validation rules.
/// </summary>
public enum Placement
{
    /// <summary>
    /// Standard feed post (default for most platforms).
    /// </summary>
    Feed = 0,

    /// <summary>
    /// Story content (24-hour ephemeral content).
    /// </summary>
    Story = 1,

    /// <summary>
    /// Short-form video content (Instagram Reels, TikTok, YouTube Shorts).
    /// </summary>
    Reel = 2
}
