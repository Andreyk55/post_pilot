using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.CloudWatchEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Lambdas.Models;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PostPilot.Api.Lambdas;

/// <summary>
/// Dispatcher Lambda - triggered by EventBridge every minute.
/// Queries for due posts and sends them to SQS for publishing.
/// </summary>
public class DispatcherFunction
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IAmazonSQS _sqsClient;
    private readonly string _queueUrl;

    public DispatcherFunction()
    {
        _serviceProvider = LambdaStartup.ConfigureServices();
        _sqsClient = new AmazonSQSClient();
        _queueUrl = Environment.GetEnvironmentVariable("SQS_QUEUE_URL")
            ?? throw new InvalidOperationException("SQS_QUEUE_URL environment variable is required.");
    }

    /// <summary>
    /// Lambda handler triggered by EventBridge scheduled rule.
    /// </summary>
    public async Task FunctionHandler(CloudWatchEvent<object> scheduledEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"Dispatcher started at {DateTime.UtcNow:O}");

        var now = DateTime.UtcNow;
        int processedCount = 0;
        int claimedCount = 0;

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Query for due posts
        var duePosts = await dbContext.Posts
            .Where(p =>
                (p.Status == PostStatus.Scheduled && p.ScheduledAt <= now) ||
                (p.Status == PostStatus.RetryPending && p.NextRetryAt != null && p.NextRetryAt <= now))
            .Select(p => new { p.Id, p.Platform, p.Status })
            .ToListAsync();

        context.Logger.LogInformation($"Found {duePosts.Count} due posts");

        foreach (var post in duePosts)
        {
            processedCount++;

            // Atomically claim the post by updating status to Publishing
            // Only succeeds if status hasn't changed since our query
            var claimed = await dbContext.Posts
                .Where(p => p.Id == post.Id && p.Status == post.Status)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PostStatus.Publishing));

            if (claimed == 0)
            {
                context.Logger.LogInformation($"Post {post.Id} already claimed by another process");
                continue;
            }

            claimedCount++;

            // Send to SQS for publishing
            var message = new PublishPostMessage
            {
                PostId = post.Id,
                Platform = post.Platform.ToString()
            };

            var sendRequest = new SendMessageRequest
            {
                QueueUrl = _queueUrl,
                MessageBody = JsonSerializer.Serialize(message),
                MessageGroupId = post.Id.ToString(), // For FIFO queue deduplication
                MessageDeduplicationId = $"{post.Id}-{DateTime.UtcNow.Ticks}" // Unique per attempt
            };

            try
            {
                await _sqsClient.SendMessageAsync(sendRequest);
                context.Logger.LogInformation($"Sent post {post.Id} to SQS");
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Failed to send post {post.Id} to SQS: {ex.Message}");

                // Revert status back so it can be picked up next run
                await dbContext.Posts
                    .Where(p => p.Id == post.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, post.Status));
            }
        }

        context.Logger.LogInformation($"Dispatcher completed. Processed: {processedCount}, Claimed: {claimedCount}");
    }
}
