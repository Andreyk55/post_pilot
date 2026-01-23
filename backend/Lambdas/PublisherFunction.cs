using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Microsoft.Extensions.DependencyInjection;
using PostPilot.Api.Enums;
using PostPilot.Api.Lambdas.Models;
using PostPilot.Api.Services.Publishing;

namespace PostPilot.Api.Lambdas;

/// <summary>
/// Publisher Lambda - triggered by SQS messages.
/// Publishes posts to social media platforms (Meta/Facebook).
/// </summary>
public class PublisherFunction
{
    private readonly IServiceProvider _serviceProvider;

    public PublisherFunction()
    {
        _serviceProvider = LambdaStartup.ConfigureServices();
    }

    /// <summary>
    /// Lambda handler triggered by SQS queue.
    /// Processes one message at a time (batch size = 1 recommended).
    /// </summary>
    public async Task<SQSBatchResponse> FunctionHandler(SQSEvent sqsEvent, ILambdaContext context)
    {
        context.Logger.LogInformation($"Publisher received {sqsEvent.Records.Count} messages");

        var batchResponse = new SQSBatchResponse
        {
            BatchItemFailures = new List<SQSBatchResponse.BatchItemFailure>()
        };

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                await ProcessMessageAsync(record, context);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Failed to process message {record.MessageId}: {ex.Message}");

                // Report failure so SQS can retry or move to DLQ
                batchResponse.BatchItemFailures.Add(new SQSBatchResponse.BatchItemFailure
                {
                    ItemIdentifier = record.MessageId
                });
            }
        }

        context.Logger.LogInformation($"Publisher completed. Failures: {batchResponse.BatchItemFailures.Count}");
        return batchResponse;
    }

    private async Task ProcessMessageAsync(SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var message = JsonSerializer.Deserialize<PublishPostMessage>(record.Body);
        if (message == null)
        {
            context.Logger.LogError($"Failed to deserialize message: {record.Body}");
            return; // Don't retry malformed messages
        }

        context.Logger.LogInformation($"Processing post {message.PostId} for platform {message.Platform}");

        // Parse platform enum
        if (!Enum.TryParse<Platform>(message.Platform, out var platform))
        {
            context.Logger.LogError($"Unknown platform: {message.Platform}");
            return; // Don't retry unknown platforms
        }

        // Create a fresh scope for this message
        using var scope = _serviceProvider.CreateScope();
        var publisherResolver = scope.ServiceProvider.GetRequiredService<IPostPublisherResolver>();

        var publisher = publisherResolver.GetPublisher(platform);
        if (publisher == null)
        {
            context.Logger.LogError($"No publisher available for platform {platform}");
            return;
        }

        // Publish the post - the publisher handles:
        // - Idempotency (checks if already published)
        // - Status updates (Published/Failed/RetryPending)
        // - Retry scheduling (sets NextRetryAt for transient errors)
        var result = await publisher.PublishAsync(message.PostId);

        if (result.Success)
        {
            context.Logger.LogInformation($"Successfully published post {message.PostId}. ExternalId: {result.ExternalPostId}");
        }
        else if (result.ErrorType == PublishErrorType.Transient)
        {
            context.Logger.LogWarning($"Transient error for post {message.PostId}: {result.ErrorMessage}. Will retry.");
            // Don't throw - the publisher already set RetryPending status and NextRetryAt
            // Dispatcher will pick it up on next poll
        }
        else
        {
            context.Logger.LogError($"Permanent error for post {message.PostId}: {result.ErrorMessage}");
            // Don't throw - the publisher already marked it as Failed
        }
    }
}
