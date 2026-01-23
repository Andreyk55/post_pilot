using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;

namespace PostPilot.Api.Services.Scheduling;

/// <summary>
/// Background service that polls for due posts in local development mode.
/// Runs every 30 seconds and triggers publishing for posts that are due.
/// </summary>
public class LocalSchedulerBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LocalSchedulerBackgroundService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(30);

    public LocalSchedulerBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<LocalSchedulerBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Local scheduler background service started (polling every {Interval}s)",
            _pollInterval.TotalSeconds);

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

        _logger.LogInformation("Local scheduler background service stopped");
    }

    private async Task ProcessDuePostsAsync(CancellationToken cancellationToken)
    {
        List<(Guid Id, Platform Platform)> duePosts;

        // First scope: just query for due posts
        using (var scope = _serviceProvider.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            // Find posts that are due for publication (Facebook only for now)
            duePosts = await dbContext.Posts
                .Where(p => p.Platform == Platform.Facebook)
                .Where(p =>
                    (p.Status == PostStatus.Pending && p.ScheduledAt <= now) ||
                    (p.Status == PostStatus.RetryPending && p.NextRetryAt != null && p.NextRetryAt <= now))
                .Select(p => new { p.Id, p.Platform })
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ContinueWith(t => t.Result.Select(p => (p.Id, p.Platform)).ToList(), cancellationToken);
        }

        if (duePosts.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Found {Count} due posts to process", duePosts.Count);

        // Process each post in its own scope
        foreach (var duePost in duePosts)
        {
            _logger.LogInformation("Processing due post {PostId}", duePost.Id);

            try
            {
                // Create a fresh scope for each publish operation
                using var publishScope = _serviceProvider.CreateScope();
                var publisherResolver = publishScope.ServiceProvider.GetRequiredService<IPostPublisherResolver>();

                var publisher = publisherResolver.GetPublisher(duePost.Platform);
                if (publisher == null)
                {
                    _logger.LogWarning("No publisher available for platform {Platform}", duePost.Platform);
                    continue;
                }

                await publisher.PublishAsync(duePost.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish post {PostId}", duePost.Id);
            }
        }
    }
}
