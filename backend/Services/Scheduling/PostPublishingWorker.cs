using Microsoft.EntityFrameworkCore;
using PostPilot.Api;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Scheduling;

/// <summary>
/// Background service that polls for due posts and publishes them.
/// Runs every 30 seconds: selects due posts, claims them atomically,
/// publishes via the appropriate publisher, and recovers stuck posts.
/// </summary>
public class PostPublishingWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<PostPublishingWorker> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _stuckPostThresholdMinutes;
    private readonly int _stuckPostRetryDelaySeconds;

    public PostPublishingWorker(
        IServiceProvider serviceProvider,
        ILogger<PostPublishingWorker> logger,
        PublishingOptions publishingOptions)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _pollInterval = TimeSpan.FromSeconds(publishingOptions.WorkerPollIntervalSeconds);
        _stuckPostThresholdMinutes = publishingOptions.StuckPostThresholdMinutes;
        _stuckPostRetryDelaySeconds = publishingOptions.StuckPostRetryDelaySeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(PostPilotLogEvents.RetryStart,
            "SCHEDULER_START polling every {Interval}s", _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDuePostsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing due posts");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation(PostPilotLogEvents.RetryStop, "SCHEDULER_STOP");
    }

    private async Task ProcessDuePostsAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, Platform Platform, PostType PostType)> duePosts;

        // First scope: just query for due posts
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var stuckThreshold = now.AddMinutes(-_stuckPostThresholdMinutes); // Posts stuck in Publishing

            // Recover posts stuck in Publishing status (safety net for crashes)
            // Increment RetryCount; if max retries exceeded, fail permanently.
            var stuckPosts = await dbContext.Posts
                .Where(p => p.Status == PostStatus.Publishing && p.UpdatedAt < stuckThreshold)
                .ToListAsync(cancellationToken);

            foreach (var stuck in stuckPosts)
            {
                var previousUpdatedAt = stuck.UpdatedAt;
                stuck.RetryCount++;
                stuck.UpdatedAt = now;

                if (stuck.RetryCount >= stuck.MaxRetries)
                {
                    stuck.Status = PostStatus.Failed;
                    stuck.ErrorMessage = $"Stuck in Publishing for >{_stuckPostThresholdMinutes} minutes (recovered {stuck.RetryCount}/{stuck.MaxRetries} times)";
                    stuck.NextRetryAt = null;

                    _logger.LogWarning(
                        "Stuck Publishing post failed permanently: PostId={PostId} PreviousUpdatedAt={PreviousUpdatedAt} Status=Failed RetryCount={RetryCount}/{MaxRetries}",
                        stuck.Id, previousUpdatedAt, stuck.RetryCount, stuck.MaxRetries);
                }
                else
                {
                    stuck.Status = PostStatus.RetryPending;
                    stuck.NextRetryAt = now.AddSeconds(_stuckPostRetryDelaySeconds);
                    stuck.ErrorMessage = $"Recovered from stuck Publishing (attempt {stuck.RetryCount}/{stuck.MaxRetries})";

                    _logger.LogWarning(
                        "Stuck Publishing post recovered: PostId={PostId} PreviousUpdatedAt={PreviousUpdatedAt} Status=RetryPending RetryCount={RetryCount}/{MaxRetries}",
                        stuck.Id, previousUpdatedAt, stuck.RetryCount, stuck.MaxRetries);
                }
            }

            if (stuckPosts.Count > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogWarning("Processed {Count} posts stuck in Publishing status", stuckPosts.Count);
            }

            // Find posts that are due for publication (Facebook + Instagram).
            // PUBLISH GATE: the target asset AND its parent MetaConnection must be
            // both connected (IsConnected) AND Active (Status). ReauthRequired holds
            // ownership but blocks publishing until the user reconnects — publishing
            // with an invalid token would just fail and re-flag. (Ownership blocking
            // elsewhere uses IsConnected alone; publishing additionally requires Active.)
            // MetaOAuthService cancels active posts up-front when an asset is disconnected,
            // but this is the backstop that prevents any straggler from being published.
            duePosts = await dbContext.Posts
                .Where(p => p.Platform == Platform.Facebook || p.Platform == Platform.Instagram)
                .Where(p =>
                    (p.Platform == Platform.Facebook
                        && p.TargetPage != null
                        && p.TargetPage.IsConnected
                        && p.TargetPage.Status == ConnectionStatus.Active
                        && (p.TargetPage.MetaConnection == null
                            || (p.TargetPage.MetaConnection.IsConnected
                                && p.TargetPage.MetaConnection.Status == ConnectionStatus.Active)))
                    || (p.Platform == Platform.Instagram
                        && p.TargetInstagramAccount != null
                        && p.TargetInstagramAccount.IsConnected
                        && p.TargetInstagramAccount.Status == ConnectionStatus.Active
                        && (p.TargetInstagramAccount.MetaConnection == null
                            || (p.TargetInstagramAccount.MetaConnection.IsConnected
                                && p.TargetInstagramAccount.MetaConnection.Status == ConnectionStatus.Active))))
                .Where(p =>
                    (p.Status == PostStatus.Scheduled && p.ScheduledAt <= now) ||
                    ((p.Status == PostStatus.RetryPending || p.Status == PostStatus.Processing) && p.NextRetryAt != null && p.NextRetryAt <= now))
                .Select(p => new { p.Id, p.Platform, p.PostType })
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ContinueWith(t => t.Result.Select(p => (p.Id, p.Platform, p.PostType)).ToList(), cancellationToken);
        }

        if (duePosts.Count == 0)
        {
            return;
        }

        _logger.LogInformation(PostPilotLogEvents.RetryStart,
            "SCHEDULER_DISPATCH count={Count}", duePosts.Count);

        // Process each post in its own scope
        foreach (var duePost in duePosts)
        {
            _logger.LogInformation("Processing due post {PostId} (type={PostType})", duePost.Id, duePost.PostType);

            try
            {
                // Create a fresh scope for each publish operation
                using var publishScope = _serviceProvider.CreateScope();

                if (duePost.PostType == PostType.Story)
                {
                    // Route stories to story publishers
                    var storyResolver = publishScope.ServiceProvider.GetRequiredService<IStoryPublisherResolver>();
                    var storyPublisher = storyResolver.GetPublisher(duePost.Platform);
                    if (storyPublisher == null)
                    {
                        _logger.LogWarning("No story publisher available for platform {Platform}", duePost.Platform);
                        continue;
                    }

                    await storyPublisher.PublishAsync(duePost.Id, cancellationToken);
                }
                else
                {
                    // Route feed posts to feed publishers
                    var publisherResolver = publishScope.ServiceProvider.GetRequiredService<IPostPublisherResolver>();
                    var publisher = publisherResolver.GetPublisher(duePost.Platform);
                    if (publisher == null)
                    {
                        _logger.LogWarning("No publisher available for platform {Platform}", duePost.Platform);
                        continue;
                    }

                    await publisher.PublishAsync(duePost.Id, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish post {PostId}", duePost.Id);
            }
        }
    }
}
