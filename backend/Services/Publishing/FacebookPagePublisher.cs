using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Publisher implementation for Facebook Pages using Meta Graph API.
/// </summary>
public class FacebookPagePublisher : IPostPublisher
{
    private readonly AppDbContext _dbContext;
    private readonly IPostScheduler _scheduler;
    private readonly HttpClient _httpClient;
    private readonly ILogger<FacebookPagePublisher> _logger;

    private const string GraphApiBaseUrl = "https://graph.facebook.com/v21.0";

    // Meta error codes - transient (retry)
    private static readonly HashSet<int> TransientErrorCodes = new()
    {
        1,    // Unknown error
        2,    // Service temporarily unavailable
        4,    // Too many calls
        17,   // User request limit reached
        341,  // Temporarily blocked
        368,  // Temporarily blocked for policies violation
        506   // Duplicate post (can be transient in some cases)
    };

    // Meta error codes - permanent (don't retry)
    private static readonly HashSet<int> PermanentErrorCodes = new()
    {
        10,   // Permission denied
        100,  // Invalid parameter
        102,  // Session invalidated
        190,  // Access token expired or invalid
        200,  // Permission error
        210,  // User not visible
        220,  // Application does not have permission
        230,  // Incorrect permission
        240,  // Desktop app cannot call this function
        250,  // Insufficient permission
        260,  // Terms of service not accepted
        270,  // Permission revoked
        294   // App not installed
    };

    public Platform SupportedPlatform => Platform.Facebook;

    public FacebookPagePublisher(
        AppDbContext dbContext,
        IPostScheduler scheduler,
        HttpClient httpClient,
        ILogger<FacebookPagePublisher> logger)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting publish for post {PostId}", postId);

        // Step 1: Load post with target page
        var post = await _dbContext.Posts
            .Include(p => p.TargetPage)
            .FirstOrDefaultAsync(p => p.Id == postId, cancellationToken);

        if (post == null)
        {
            _logger.LogWarning("Post {PostId} not found", postId);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Post not found");
        }

        // Step 2: Idempotency check - already published?
        if (post.Status == PostStatus.Published && !string.IsNullOrEmpty(post.ExternalPostId))
        {
            _logger.LogInformation("Post {PostId} already published as {ExternalPostId}",
                postId, post.ExternalPostId);
            return new PublishResult(true, ExternalPostId: post.ExternalPostId,
                ErrorType: PublishErrorType.AlreadyPublished);
        }

        // Step 3: Atomically claim the post (prevent race conditions)
        var claimResult = await TryClaimPostAsync(post, cancellationToken);
        if (!claimResult)
        {
            _logger.LogInformation("Post {PostId} already being processed by another worker", postId);
            return new PublishResult(false, ErrorType: PublishErrorType.AlreadyPublished,
                ErrorMessage: "Post is being processed by another worker");
        }

        // Step 4: Validate prerequisites
        if (post.TargetPage == null || string.IsNullOrEmpty(post.TargetPage.AccessToken))
        {
            await MarkFailedAsync(post, "No target page or access token configured", cancellationToken);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "No target page configured");
        }

        // Step 5: Call Meta Graph API
        try
        {
            var result = await CallMetaApiAsync(post, cancellationToken);

            if (result.Success)
            {
                await MarkPublishedAsync(post, result.ExternalPostId!, cancellationToken);
                return result;
            }
            else
            {
                return await HandlePublishFailureAsync(post, result, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error publishing post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Network error: {ex.Message}"),
                cancellationToken);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout publishing post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Request timed out"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: ex.Message),
                cancellationToken);
        }
    }

    private async Task<bool> TryClaimPostAsync(Post post, CancellationToken cancellationToken)
    {
        // Optimistic concurrency - only claim if status is still Pending or RetryPending
        var rowsAffected = await _dbContext.Posts
            .Where(p => p.Id == post.Id &&
                       (p.Status == PostStatus.Pending || p.Status == PostStatus.RetryPending))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.Status, PostStatus.Publishing)
                .SetProperty(p => p.UpdatedAt, DateTime.UtcNow),
                cancellationToken);

        if (rowsAffected > 0)
        {
            post.Status = PostStatus.Publishing;
            return true;
        }

        return false;
    }

    private async Task<PublishResult> CallMetaApiAsync(Post post, CancellationToken cancellationToken)
    {
        var pageId = post.TargetPage!.PageId;
        var accessToken = post.TargetPage.AccessToken;

        string url;
        HttpContent content;

        if (!string.IsNullOrEmpty(post.MediaUrl))
        {
            // For photos, use photos endpoint
            url = $"{GraphApiBaseUrl}/{pageId}/photos";
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["message"] = post.Content,
                ["url"] = post.MediaUrl,  // Meta will fetch from this URL
                ["access_token"] = accessToken
            });
        }
        else
        {
            // Text-only post
            url = $"{GraphApiBaseUrl}/{pageId}/feed";
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["message"] = post.Content,
                ["access_token"] = accessToken
            });
        }

        _logger.LogInformation("Calling Meta API: POST {Url} for post {PostId}", url, post.Id);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogDebug("Meta API response for post {PostId}: {StatusCode} - {Body}",
            post.Id, response.StatusCode, responseBody);

        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<MetaPostResponse>(responseBody);
            var externalId = result?.Id ?? result?.PostId;

            if (string.IsNullOrEmpty(externalId))
            {
                _logger.LogWarning("Meta API returned success but no post ID for {PostId}", post.Id);
            }

            return new PublishResult(true, ExternalPostId: externalId);
        }
        else
        {
            var error = JsonSerializer.Deserialize<MetaErrorResponse>(responseBody);
            var errorCode = error?.Error?.Code ?? 0;
            var errorType = ClassifyError(errorCode);

            _logger.LogWarning("Meta API error for post {PostId}: Code={Code}, Message={Message}",
                post.Id, errorCode, error?.Error?.Message);

            return new PublishResult(false,
                ErrorType: errorType,
                ErrorMessage: error?.Error?.Message ?? $"HTTP {(int)response.StatusCode}");
        }
    }

    private PublishErrorType ClassifyError(int errorCode)
    {
        if (PermanentErrorCodes.Contains(errorCode))
            return PublishErrorType.Permanent;

        if (TransientErrorCodes.Contains(errorCode))
            return PublishErrorType.Transient;

        // HTTP 5xx errors are transient
        // Default to transient for unknown errors (safer - we'll retry)
        return PublishErrorType.Transient;
    }

    private async Task MarkPublishedAsync(Post post, string externalPostId,
        CancellationToken cancellationToken)
    {
        post.Status = PostStatus.Published;
        post.ExternalPostId = externalPostId;
        post.PublishedAt = DateTime.UtcNow;
        post.UpdatedAt = DateTime.UtcNow;
        post.ErrorMessage = null;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Post {PostId} published successfully as {ExternalPostId}",
            post.Id, externalPostId);
    }

    private async Task MarkFailedAsync(Post post, string errorMessage,
        CancellationToken cancellationToken)
    {
        post.Status = PostStatus.Failed;
        post.ErrorMessage = errorMessage;
        post.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Post {PostId} failed permanently: {Error}", post.Id, errorMessage);
    }

    private async Task<PublishResult> HandlePublishFailureAsync(
        Post post,
        PublishResult result,
        CancellationToken cancellationToken)
    {
        post.RetryCount++;
        post.ErrorMessage = result.ErrorMessage;
        post.UpdatedAt = DateTime.UtcNow;

        if (result.ErrorType == PublishErrorType.Permanent || post.RetryCount >= post.MaxRetries)
        {
            // Permanent failure or max retries exceeded
            post.Status = PostStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Post {PostId} failed permanently after {RetryCount} attempts: {Error}",
                post.Id, post.RetryCount, result.ErrorMessage);

            return result;
        }

        // Schedule retry with exponential backoff: 2, 4, 8 minutes
        var delayMinutes = Math.Pow(2, post.RetryCount);
        var retryAt = DateTime.UtcNow.AddMinutes(delayMinutes);

        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = retryAt;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Schedule the retry
        await _scheduler.ScheduleRetryAsync(post, retryAt, cancellationToken);

        _logger.LogInformation(
            "Post {PostId} scheduled for retry #{RetryCount} at {RetryAt} (in {DelayMinutes} minutes)",
            post.Id, post.RetryCount, retryAt, delayMinutes);

        return result;
    }
}

// Response models for Meta Graph API
internal class MetaPostResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("post_id")]
    public string? PostId { get; set; }
}

internal class MetaErrorResponse
{
    [JsonPropertyName("error")]
    public MetaError? Error { get; set; }
}

internal class MetaError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("error_subcode")]
    public int? ErrorSubcode { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("fbtrace_id")]
    public string? FbTraceId { get; set; }
}
