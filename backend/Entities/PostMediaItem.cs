using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

/// <summary>
/// Represents a single media item in a post (used for carousel posts with multiple images).
/// For single-media posts, the legacy Post.MediaUrl/MediaType fields are still used.
/// For carousel posts (2-10 images), media items are stored here with explicit ordering.
/// </summary>
public class PostMediaItem
{
    public Guid Id { get; set; }

    /// <summary>
    /// Parent post ID.
    /// </summary>
    public Guid PostId { get; set; }

    /// <summary>
    /// Display order (0-based). Preserved during carousel publishing.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// S3 key or external URL for this media file.
    /// </summary>
    public required string MediaUrl { get; set; }

    /// <summary>
    /// Type of media (Image or Video). For IG carousel, must be Image.
    /// </summary>
    public MediaType MediaType { get; set; } = MediaType.Image;

    // Navigation property
    public Post Post { get; set; } = null!;
}
