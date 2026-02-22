using Amazon.Lambda.Core;
using Amazon.Lambda.CloudWatchEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Lambdas;

/// <summary>
/// Stuck Recovery Lambda — triggered by EventBridge every 5 minutes.
/// Recovers posts stuck in Publishing status for >10 minutes.
/// Increments RetryCount; fails permanently if max retries exceeded.
/// Does NOT touch Processing posts (they have their own poll lifecycle).
/// </summary>
public class StuckRecoveryFunction
{
    private readonly IServiceProvider _serviceProvider;

    public StuckRecoveryFunction()
    {
        _serviceProvider = LambdaStartup.ConfigureServices();
    }

    /// <summary>
    /// Lambda handler triggered by EventBridge scheduled rule (every 5 minutes).
    /// </summary>
    public async Task FunctionHandler(CloudWatchEvent<object> scheduledEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"StuckRecovery started at {DateTime.UtcNow:O}");

        var now = DateTime.UtcNow;
        var stuckThreshold = now.AddMinutes(-10); // Posts stuck in Publishing for 10+ minutes
        int recoveredCount = 0;
        int failedCount = 0;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stuckPosts = await dbContext.Posts
            .Where(p => p.Status == PostStatus.Publishing && p.UpdatedAt < stuckThreshold)
            .ToListAsync();

        if (stuckPosts.Count == 0)
        {
            context.Logger.LogInformation("No stuck Publishing posts found");
            return;
        }

        foreach (var post in stuckPosts)
        {
            var previousUpdatedAt = post.UpdatedAt;
            post.RetryCount++;
            post.UpdatedAt = now;

            if (post.RetryCount >= post.MaxRetries)
            {
                post.Status = PostStatus.Failed;
                post.ErrorMessage = $"Stuck in Publishing for >10 minutes (recovered {post.RetryCount}/{post.MaxRetries} times)";
                post.NextRetryAt = null;
                failedCount++;

                context.Logger.LogWarning(
                    $"Stuck post failed permanently: PostId={post.Id} PreviousUpdatedAt={previousUpdatedAt:O} RetryCount={post.RetryCount}/{post.MaxRetries}");
            }
            else
            {
                post.Status = PostStatus.RetryPending;
                post.NextRetryAt = now;
                post.ErrorMessage = $"Recovered from stuck Publishing (attempt {post.RetryCount}/{post.MaxRetries})";
                recoveredCount++;

                context.Logger.LogWarning(
                    $"Stuck post recovered: PostId={post.Id} PreviousUpdatedAt={previousUpdatedAt:O} RetryCount={post.RetryCount}/{post.MaxRetries}");
            }
        }

        await dbContext.SaveChangesAsync();

        context.Logger.LogInformation(
            $"StuckRecovery completed. Total={stuckPosts.Count}, Recovered={recoveredCount}, Failed={failedCount}");
    }
}
