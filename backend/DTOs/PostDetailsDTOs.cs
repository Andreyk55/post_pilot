namespace PostPilot.Api.DTOs;

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
public record PostDetailsDto(
    Guid Id,
    string Content,
    string? MediaUrl,
    string MediaType,
    string Platform,
    DateTime ScheduledAt,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TargetPageId,
    string? TargetPageName,
    DateTime? PublishedAt,
    string? ExternalPostId,
    string? ErrorMessage,
    int RetryCount,
    PostEngagementDto? Engagement,
    string? ExternalPostUrl
);
