namespace PostPilot.Api.DTOs;

public record MediaThumbnailDto(
    string? StorageKey,
    string? Url,
    string? MimeType,
    int? Width,
    int? Height,
    long? SizeBytes,
    DateTime? CreatedAtUtc
);

/// <summary>
/// Engagement metrics for a social media post.
/// </summary>
public record PostEngagementDto(
    int? LikesCount,
    int? CommentsCount,
    int? SharesCount
);

/// <summary>
/// Extended post details including engagement metrics fetched from the platform.
/// </summary>
public record PostDetailsMediaItemDto(
    Guid Id,
    int Order,
    string MediaUrl,
    string MediaType,
    MediaThumbnailDto? Thumbnail = null
);

public record PostDetailsDto(
    Guid Id,
    string Content,
    string? MediaUrl,
    string MediaType,
    string PostType,
    string Platform,
    DateTime ScheduledAt,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TargetPageId,
    string? TargetPageName,
    Guid? TargetInstagramAccountId,
    string? TargetInstagramAccountName,
    DateTime? PublishedAt,
    string? ExternalPostId,
    string? ErrorMessage,
    int RetryCount,
    int ProcessingPollCount,
    DateTime? NextRetryAt,
    PostEngagementDto? Engagement,
    string? ExternalPostUrl,
    string? ProfileUrl,
    string? PageUrl,
    string? InstagramMediaType,
    MediaThumbnailDto? Thumbnail = null,
    List<PostDetailsMediaItemDto>? MediaItems = null,
    /// <summary>
    /// True if the post's target page/IG account is currently connected. False if it was
    /// disconnected (frontend can render a "disconnected" badge). Null if the post has no target.
    /// </summary>
    bool? TargetConnectionActive = null
);
