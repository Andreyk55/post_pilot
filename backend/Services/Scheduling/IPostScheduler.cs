using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Scheduling;

/// <summary>
/// Abstraction for scheduling post publication.
/// Implementations handle the platform-specific scheduling mechanism.
/// </summary>
public interface IPostScheduler
{
    /// <summary>
    /// Schedule a post for publication at its ScheduledAt time.
    /// Creates the appropriate trigger based on environment (EventBridge or local polling).
    /// </summary>
    /// <param name="post">The post to schedule</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing success status and optional schedule identifier</returns>
    Task<ScheduleResult> ScheduleAsync(Post post, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update an existing schedule when the ScheduledAt time changes.
    /// </summary>
    /// <param name="post">The post with updated ScheduledAt</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<ScheduleResult> RescheduleAsync(Post post, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel/delete a scheduled trigger when a post is deleted or no longer needed.
    /// </summary>
    /// <param name="post">The post whose schedule should be cancelled</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CancelScheduleAsync(Post post, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedule a retry for a failed post.
    /// </summary>
    /// <param name="post">The failed post to retry</param>
    /// <param name="retryAt">When to retry</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<ScheduleResult> ScheduleRetryAsync(Post post, DateTime retryAt, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a scheduling operation
/// </summary>
public record ScheduleResult(
    bool Success,
    string? ScheduleIdentifier = null,
    string? ErrorMessage = null
);
