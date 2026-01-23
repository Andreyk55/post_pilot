using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Scheduling;

/// <summary>
/// Local development scheduler that relies on polling.
/// Does not create external triggers - the LocalSchedulerBackgroundService
/// polls the database for due posts.
/// </summary>
public class LocalPostScheduler : IPostScheduler
{
    private readonly ILogger<LocalPostScheduler> _logger;

    public LocalPostScheduler(ILogger<LocalPostScheduler> logger)
    {
        _logger = logger;
    }

    public Task<ScheduleResult> ScheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        // In local mode, we don't create external schedules
        // The background service polls for due posts
        _logger.LogInformation(
            "Post {PostId} scheduled for {ScheduledAt} (local polling mode)",
            post.Id, post.ScheduledAt);

        return Task.FromResult(new ScheduleResult(true, ScheduleIdentifier: "local-polling"));
    }

    public Task<ScheduleResult> RescheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        // No action needed - polling will pick up new time
        _logger.LogInformation(
            "Post {PostId} rescheduled to {ScheduledAt} (local polling mode)",
            post.Id, post.ScheduledAt);

        return Task.FromResult(new ScheduleResult(true, ScheduleIdentifier: "local-polling"));
    }

    public Task CancelScheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        // No external trigger to cancel
        _logger.LogInformation("Schedule cancelled for post {PostId} (local polling mode)", post.Id);
        return Task.CompletedTask;
    }

    public Task<ScheduleResult> ScheduleRetryAsync(Post post, DateTime retryAt, CancellationToken cancellationToken = default)
    {
        // Background service will handle retry by checking NextRetryAt
        _logger.LogInformation(
            "Retry scheduled for post {PostId} at {RetryAt} (local polling mode)",
            post.Id, retryAt);

        return Task.FromResult(new ScheduleResult(true, ScheduleIdentifier: "local-polling-retry"));
    }
}
