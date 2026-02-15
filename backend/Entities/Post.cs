using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

public class Post
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public string? MediaUrl { get; set; }

    /// <summary>
    /// Type of media attached to this post (None, Image, or Video).
    /// </summary>
    public MediaType MediaType { get; set; } = MediaType.None;

    public Platform Platform { get; set; }
    public DateTime ScheduledAt { get; set; }
    public PostStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Scheduling and publishing fields

    /// <summary>
    /// Which Facebook Page to post to (references ConnectedPage.Id)
    /// </summary>
    public Guid? TargetPageId { get; set; }

    /// <summary>
    /// Which Instagram Business Account to post to (references ConnectedInstagramAccount.Id)
    /// </summary>
    public Guid? TargetInstagramAccountId { get; set; }

    /// <summary>
    /// External URL to the published post (e.g., Instagram permalink)
    /// </summary>
    public string? ExternalPostUrl { get; set; }

    /// <summary>
    /// Timestamp when the post was actually published
    /// </summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>
    /// External ID returned by Meta Graph API (e.g., "page-id_post-id")
    /// Used for idempotency and linking back to Facebook
    /// </summary>
    public string? ExternalPostId { get; set; }

    /// <summary>
    /// Last error message for debugging failed posts
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Current retry attempt count
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Maximum retry attempts before marking as permanently failed
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Next scheduled retry time (for retry scheduling)
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// AWS EventBridge Schedule ARN (for cleanup when post is deleted/updated)
    /// Only populated in production environment
    /// </summary>
    public string? ScheduleArn { get; set; }

    /// <summary>
    /// Custom thumbnail URL for video posts (user-selected from AI-suggested frames).
    /// If null, uses the default first frame.
    /// </summary>
    public string? SelectedThumbnailUrl { get; set; }

    /// <summary>
    /// Instagram media type (IMAGE, REELS, VIDEO, CAROUSEL_ALBUM) as returned by Graph API.
    /// Populated after successful IG publish. Null for non-Instagram posts or pre-migration rows.
    /// </summary>
    public InstagramMediaType? InstagramMediaType { get; set; }

    /// <summary>
    /// Instagram container creation ID returned from POST /{ig-user-id}/media.
    /// Used for stateful video publishing: create container → poll status → publish.
    /// Persisted so the publisher can resume polling across multiple attempts.
    /// </summary>
    public string? InstagramCreationId { get; set; }

    /// <summary>
    /// Number of times we've polled the IG container status while it's IN_PROGRESS.
    /// Separate from RetryCount (which tracks actual failures).
    /// If this exceeds MaxProcessingPollCount, the post fails with a timeout.
    /// </summary>
    public int ProcessingPollCount { get; set; }

    /// <summary>
    /// Maximum processing poll attempts before timing out (default ~10 minutes at 30s intervals).
    /// </summary>
    public const int MaxProcessingPollCount = 20;

    /// <summary>
    /// JSON array of child container creation IDs for Instagram carousel publishing.
    /// Stored so the publisher can skip already-created children on retry.
    /// Example: ["17889455560051234","17889455560051235"]
    /// </summary>
    public string? InstagramChildCreationIds { get; set; }

    /// <summary>
    /// Carousel container creation ID (the parent container that references children).
    /// Used for stateful carousel publishing: create children → create carousel → poll → publish.
    /// </summary>
    public string? InstagramCarouselCreationId { get; set; }

    // Navigation properties
    public ConnectedPage? TargetPage { get; set; }
    public ConnectedInstagramAccount? TargetInstagramAccount { get; set; }

    /// <summary>
    /// Media items for multi-image posts (carousel). Ordered by PostMediaItem.Order.
    /// For single-media posts, the legacy MediaUrl/MediaType fields are used instead.
    /// </summary>
    public List<PostMediaItem> MediaItems { get; set; } = new();
}
