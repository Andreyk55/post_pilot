using System.Text.Json;
using Amazon.Scheduler;
using Amazon.Scheduler.Model;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Scheduling;

/// <summary>
/// Production scheduler using AWS EventBridge Scheduler.
/// Creates one-time schedules that trigger at the exact ScheduledAt time.
/// </summary>
public class EventBridgePostScheduler : IPostScheduler
{
    private readonly IAmazonScheduler _scheduler;
    private readonly EventBridgeSchedulerSettings _settings;
    private readonly ILogger<EventBridgePostScheduler> _logger;

    public EventBridgePostScheduler(
        IAmazonScheduler scheduler,
        EventBridgeSchedulerSettings settings,
        ILogger<EventBridgePostScheduler> logger)
    {
        _scheduler = scheduler;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ScheduleResult> ScheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        try
        {
            var scheduleName = GetScheduleName(post.Id);

            var request = new CreateScheduleRequest
            {
                Name = scheduleName,
                GroupName = _settings.ScheduleGroupName,
                FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF },
                ScheduleExpression = $"at({post.ScheduledAt:yyyy-MM-ddTHH:mm:ss})",
                ScheduleExpressionTimezone = "UTC",
                Target = new Target
                {
                    Arn = _settings.PublisherLambdaArn,
                    RoleArn = _settings.SchedulerRoleArn,
                    Input = JsonSerializer.Serialize(new { postId = post.Id })
                },
                ActionAfterCompletion = ActionAfterCompletion.DELETE // One-time schedule, auto-cleanup
            };

            var response = await _scheduler.CreateScheduleAsync(request, cancellationToken);

            _logger.LogInformation(
                "Created EventBridge schedule {ScheduleName} for post {PostId} at {ScheduledAt}",
                scheduleName, post.Id, post.ScheduledAt);

            return new ScheduleResult(true, ScheduleIdentifier: response.ScheduleArn);
        }
        catch (ConflictException)
        {
            // Schedule already exists - update it instead
            _logger.LogInformation("Schedule already exists for post {PostId}, updating", post.Id);
            return await RescheduleAsync(post, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create EventBridge schedule for post {PostId}", post.Id);
            return new ScheduleResult(false, ErrorMessage: ex.Message);
        }
    }

    public async Task<ScheduleResult> RescheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        try
        {
            var scheduleName = GetScheduleName(post.Id);

            var request = new UpdateScheduleRequest
            {
                Name = scheduleName,
                GroupName = _settings.ScheduleGroupName,
                FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF },
                ScheduleExpression = $"at({post.ScheduledAt:yyyy-MM-ddTHH:mm:ss})",
                ScheduleExpressionTimezone = "UTC",
                Target = new Target
                {
                    Arn = _settings.PublisherLambdaArn,
                    RoleArn = _settings.SchedulerRoleArn,
                    Input = JsonSerializer.Serialize(new { postId = post.Id })
                },
                ActionAfterCompletion = ActionAfterCompletion.DELETE
            };

            var response = await _scheduler.UpdateScheduleAsync(request, cancellationToken);

            _logger.LogInformation(
                "Updated EventBridge schedule {ScheduleName} for post {PostId} to {ScheduledAt}",
                scheduleName, post.Id, post.ScheduledAt);

            return new ScheduleResult(true, ScheduleIdentifier: response.ScheduleArn);
        }
        catch (ResourceNotFoundException)
        {
            // Schedule doesn't exist - create it
            _logger.LogInformation("Schedule not found for post {PostId}, creating new", post.Id);
            return await ScheduleAsync(post, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reschedule post {PostId}", post.Id);
            return new ScheduleResult(false, ErrorMessage: ex.Message);
        }
    }

    public async Task CancelScheduleAsync(Post post, CancellationToken cancellationToken = default)
    {
        try
        {
            var scheduleName = GetScheduleName(post.Id);

            await _scheduler.DeleteScheduleAsync(new DeleteScheduleRequest
            {
                Name = scheduleName,
                GroupName = _settings.ScheduleGroupName
            }, cancellationToken);

            _logger.LogInformation("Deleted EventBridge schedule for post {PostId}", post.Id);
        }
        catch (ResourceNotFoundException)
        {
            _logger.LogDebug("Schedule for post {PostId} already deleted or doesn't exist", post.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete EventBridge schedule for post {PostId}", post.Id);
        }
    }

    public async Task<ScheduleResult> ScheduleRetryAsync(Post post, DateTime retryAt, CancellationToken cancellationToken = default)
    {
        try
        {
            // Create a uniquely named schedule for the retry
            var scheduleName = GetRetryScheduleName(post.Id, post.RetryCount);

            var request = new CreateScheduleRequest
            {
                Name = scheduleName,
                GroupName = _settings.ScheduleGroupName,
                FlexibleTimeWindow = new FlexibleTimeWindow { Mode = FlexibleTimeWindowMode.OFF },
                ScheduleExpression = $"at({retryAt:yyyy-MM-ddTHH:mm:ss})",
                ScheduleExpressionTimezone = "UTC",
                Target = new Target
                {
                    Arn = _settings.PublisherLambdaArn,
                    RoleArn = _settings.SchedulerRoleArn,
                    Input = JsonSerializer.Serialize(new { postId = post.Id, isRetry = true })
                },
                ActionAfterCompletion = ActionAfterCompletion.DELETE
            };

            var response = await _scheduler.CreateScheduleAsync(request, cancellationToken);

            _logger.LogInformation(
                "Created retry schedule {ScheduleName} for post {PostId} at {RetryAt} (attempt {RetryCount})",
                scheduleName, post.Id, retryAt, post.RetryCount);

            return new ScheduleResult(true, ScheduleIdentifier: response.ScheduleArn);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create retry schedule for post {PostId}", post.Id);
            return new ScheduleResult(false, ErrorMessage: ex.Message);
        }
    }

    private static string GetScheduleName(Guid postId) => $"postpilot-publish-{postId}";

    private static string GetRetryScheduleName(Guid postId, int retryCount) =>
        $"postpilot-retry-{postId}-{retryCount}";
}
