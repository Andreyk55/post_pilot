namespace PostPilot.Api.Enums;

/// <summary>
/// Instagram media type as returned by the Graph API (media_type field).
/// Stored on Post after publishing to distinguish IG content types.
/// </summary>
public enum InstagramMediaType
{
    /// <summary>
    /// Unknown or not yet fetched (default for pre-existing rows).
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Single image post.
    /// </summary>
    Image = 1,

    /// <summary>
    /// Video post (non-Reels video, rarely used today).
    /// </summary>
    Video = 2,

    /// <summary>
    /// Reels short-form video.
    /// </summary>
    Reels = 3,

    /// <summary>
    /// Carousel album (multiple images/videos).
    /// </summary>
    CarouselAlbum = 4,
}
