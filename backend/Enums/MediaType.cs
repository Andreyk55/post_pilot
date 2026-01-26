namespace PostPilot.Api.Enums;

/// <summary>
/// Type of media attached to a post.
/// </summary>
public enum MediaType
{
    /// <summary>
    /// No media attached (text-only post).
    /// </summary>
    None = 0,

    /// <summary>
    /// Image attachment (JPEG, PNG, GIF).
    /// </summary>
    Image = 1,

    /// <summary>
    /// Video attachment (MP4).
    /// </summary>
    Video = 2
}
