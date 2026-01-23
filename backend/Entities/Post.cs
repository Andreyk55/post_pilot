using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

public class Post
{
    public Guid Id { get; set; }
    public required string Content { get; set; }
    public string? MediaUrl { get; set; }
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

    // Navigation property
    public ConnectedPage? TargetPage { get; set; }
}
