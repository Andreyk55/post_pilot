using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Scheduling;

/// <summary>
/// Polling-based post scheduler. Does not create external triggers —
/// the <see cref="PostPublishingWorker"/> polls the database for due posts.
/// </summary>
public class PostScheduler : IPostScheduler
{
    private readonly ILogger<PostScheduler> _logger;

    public PostScheduler(ILogger<PostScheduler> logger)
    {
        _logger = logger;
    }

    public Task<ScheduleResult> ScheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Post {PostId} scheduled for {ScheduledAt} (polling mode)",
            post.Id, post.ScheduledAt);

        return Task.FromResult(new ScheduleResult(true, ScheduleIdentifier: "local-polling"));
    }

    public Task<ScheduleResult> RescheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Post {PostId} rescheduled to {ScheduledAt} (polling mode)",
            post.Id, post.ScheduledAt);

        return Task.FromResult(new ScheduleResult(true, ScheduleIdentifier: "local-polling"));
    }

    public Task CancelScheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Schedule cancelled for post {PostId} (polling mode)", post.Id);
        return Task.CompletedTask;
    }

    public Task<ScheduleResult> ScheduleRetryAsync(Post post, DateTime retryAt, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Retry scheduled for post {PostId} at {RetryAt} (polling mode)",
            post.Id, retryAt);

        return Task.FromResult(new ScheduleResult(true, ScheduleIdentifier: "local-polling-retry"));
    }
}
