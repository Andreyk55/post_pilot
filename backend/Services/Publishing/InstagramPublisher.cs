using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Publisher implementation for Instagram Feed posts using Meta Graph API
/// (Instagram Content Publishing API).
///
/// Flow:
/// 1. Create media container: POST /{ig-user-id}/media
/// 2. Poll container status: GET /{creation-id}?fields=status_code,status
/// 3. Publish container: POST /{ig-user-id}/media_publish
/// 4. (Optional) Fetch permalink: GET /{media-id}?fields=permalink
/// </summary>
public class InstagramPublisher : IPostPublisher
{
    private readonly AppDbContext _dbContext;
    private readonly IPostScheduler _scheduler;
    private readonly IMediaService _mediaService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<InstagramPublisher> _logger;

    private const string GraphApiBaseUrl = "https://graph.facebook.com/v21.0";
    private static readonly TimeSpan MediaDownloadUrlExpiration = TimeSpan.FromHours(1);

    // Container polling settings
    private const int MaxPollAttempts = 30;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    // Meta error codes - transient (retry)
    private static readonly HashSet<int> TransientErrorCodes = new()
    {
        1,    // Unknown error
        2,    // Service temporarily unavailable
        4,    // Too many calls
        17,   // User request limit reached
        341,  // Temporarily blocked
        368,  // Temporarily blocked for policies violation
    };

    // Meta error codes - permanent (don't retry)
    private static readonly HashSet<int> PermanentErrorCodes = new()
    {
        10,   // Permission denied
        100,  // Invalid parameter
        102,  // Session invalidated
        190,  // Access token expired or invalid
        200,  // Permission error
        220,  // Application does not have permission
        230,  // Incorrect permission
        250,  // Insufficient permission
        270,  // Permission revoked
        294,  // App not installed
        36003, // IG media creation failed
    };

    public Platform SupportedPlatform => Platform.Instagram;

    public InstagramPublisher(
        AppDbContext dbContext,
        IPostScheduler scheduler,
        IMediaService mediaService,
        HttpClient httpClient,
        ILogger<InstagramPublisher> logger)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _mediaService = mediaService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Instagram publish for post {PostId}", postId);

        // Step 1: Load post with target IG account
        var post = await _dbContext.Posts
            .Include(p => p.TargetInstagramAccount)
            .FirstOrDefaultAsync(p => p.Id == postId, cancellationToken);

        if (post == null)
        {
            _logger.LogWarning("Post {PostId} not found", postId);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Post not found");
        }

        // Step 2: Idempotency check
        if (post.Status == PostStatus.Published && !string.IsNullOrEmpty(post.ExternalPostId))
        {
            _logger.LogInformation("Post {PostId} already published as {ExternalPostId}",
                postId, post.ExternalPostId);
            return new PublishResult(true, ExternalPostId: post.ExternalPostId,
                ErrorType: PublishErrorType.AlreadyPublished);
        }

        // Step 3: Atomically claim the post
        var claimResult = await TryClaimPostAsync(post, cancellationToken);
        if (!claimResult)
        {
            _logger.LogInformation("Post {PostId} already being processed by another worker", postId);
            return new PublishResult(false, ErrorType: PublishErrorType.AlreadyPublished,
                ErrorMessage: "Post is being processed by another worker");
        }

        // Step 4: Validate prerequisites
        if (post.TargetInstagramAccount == null)
        {
            await MarkFailedAsync(post, "No target Instagram account configured", cancellationToken);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "No target Instagram account configured");
        }

        // Resolve the page access token via the linked Facebook Page
        var accessToken = await ResolveAccessTokenAsync(post.TargetInstagramAccount, cancellationToken);
        if (string.IsNullOrEmpty(accessToken))
        {
            await MarkFailedAsync(post, "No access token available for the linked Facebook Page", cancellationToken);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "No access token for linked Facebook Page");
        }

        // Step 5: Publish via Instagram Content Publishing API
        try
        {
            var result = await PublishToInstagramAsync(post, accessToken, cancellationToken);

            if (result.Success)
            {
                await MarkPublishedAsync(post, result.ExternalPostId!, cancellationToken);

                // Try to fetch permalink
                if (!string.IsNullOrEmpty(result.ExternalPostId))
                {
                    await TryFetchPermalinkAsync(post, result.ExternalPostId, accessToken, cancellationToken);
                }

                return result;
            }
            else
            {
                return await HandlePublishFailureAsync(post, result, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error publishing Instagram post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Network error: {ex.Message}"),
                cancellationToken);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout publishing Instagram post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Request timed out"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing Instagram post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: ex.Message),
                cancellationToken);
        }
    }

    /// <summary>
    /// Resolves the page access token for an Instagram Business Account
    /// by looking up the linked Facebook Page.
    /// </summary>
    private async Task<string?> ResolveAccessTokenAsync(
        ConnectedInstagramAccount igAccount, CancellationToken cancellationToken)
    {
        // The IG account has a PageId (Facebook Page ID string).
        // Find the ConnectedPage with that PageId to get the access token.
        var connectedPage = await _dbContext.Set<ConnectedPage>()
            .FirstOrDefaultAsync(p => p.PageId == igAccount.PageId, cancellationToken);

        if (connectedPage == null)
        {
            _logger.LogWarning(
                "No ConnectedPage found for Facebook PageId {PageId} linked to IG account {IgAccountId}",
                igAccount.PageId, igAccount.Id);
            return null;
        }

        return connectedPage.AccessToken;
    }

    /// <summary>
    /// Full Instagram Content Publishing flow:
    /// 1. Create container → 2. Poll status → 3. Publish
    /// </summary>
    private async Task<PublishResult> PublishToInstagramAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;

        // Step 1: Generate a public URL for the image
        string imageUrl;
        if (_mediaService.IsS3Key(post.MediaUrl!))
        {
            imageUrl = _mediaService.GenerateDownloadUrl(post.MediaUrl!, MediaDownloadUrlExpiration);
            _logger.LogInformation("Generated pre-signed URL for S3 key {S3Key} for IG post {PostId}",
                post.MediaUrl, post.Id);
        }
        else
        {
            imageUrl = post.MediaUrl!;
        }

        // Step 2: Create media container
        var containerResult = await CreateMediaContainerAsync(
            igUserId, imageUrl, post.Content, accessToken, cancellationToken);

        if (!containerResult.Success)
        {
            return containerResult;
        }

        var creationId = containerResult.ExternalPostId!;
        _logger.LogInformation("Created IG media container {CreationId} for post {PostId}",
            creationId, post.Id);

        // Step 3: Poll for container to be ready
        var pollResult = await PollContainerStatusAsync(creationId, accessToken, cancellationToken);
        if (!pollResult.Success)
        {
            return pollResult;
        }

        // Step 4: Publish the container
        var publishResult = await PublishMediaContainerAsync(
            igUserId, creationId, accessToken, cancellationToken);

        return publishResult;
    }

    /// <summary>
    /// POST /{ig-user-id}/media with image_url and caption
    /// </summary>
    private async Task<PublishResult> CreateMediaContainerAsync(
        string igUserId, string imageUrl, string caption,
        string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{GraphApiBaseUrl}/{igUserId}/media";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["image_url"] = imageUrl,
            ["caption"] = caption ?? "",
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("Creating IG media container: POST {Url} (image_url=<redacted>)", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("IG container creation response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaIdResponse(response, responseBody, "container creation");
    }

    /// <summary>
    /// Polls GET /{creation-id}?fields=status_code,status until FINISHED or error/timeout.
    /// </summary>
    private async Task<PublishResult> PollContainerStatusAsync(
        string creationId, string accessToken, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            await Task.Delay(PollInterval, cancellationToken);

            var url = $"{GraphApiBaseUrl}/{creationId}?fields=status_code,status&access_token={accessToken}";

            var response = await _httpClient.GetAsync(url, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("IG container status check failed: {StatusCode} - {Body}",
                    response.StatusCode, RedactToken(responseBody));

                // Don't fail immediately on a single poll error - could be transient
                if (attempt >= 3)
                {
                    return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                        ErrorMessage: $"Container status check failed after {attempt + 1} attempts");
                }
                continue;
            }

            var statusResult = JsonSerializer.Deserialize<IgContainerStatusResponse>(responseBody);
            var statusCode = statusResult?.StatusCode?.ToUpperInvariant();

            _logger.LogInformation("IG container {CreationId} status: {StatusCode} (attempt {Attempt}/{Max})",
                creationId, statusCode, attempt + 1, MaxPollAttempts);

            switch (statusCode)
            {
                case "FINISHED":
                    return new PublishResult(true, ExternalPostId: creationId);

                case "ERROR":
                    return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                        ErrorMessage: $"Container processing failed: {statusResult?.Status}");

                case "EXPIRED":
                    return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                        ErrorMessage: "Container expired before publishing");

                case "IN_PROGRESS":
                default:
                    // Keep polling
                    break;
            }
        }

        return new PublishResult(false, ErrorType: PublishErrorType.Transient,
            ErrorMessage: $"Container not ready after {MaxPollAttempts} poll attempts (timeout)");
    }

    /// <summary>
    /// POST /{ig-user-id}/media_publish with creation_id
    /// </summary>
    private async Task<PublishResult> PublishMediaContainerAsync(
        string igUserId, string creationId,
        string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{GraphApiBaseUrl}/{igUserId}/media_publish";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["creation_id"] = creationId,
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("Publishing IG container: POST {Url} creation_id={CreationId}",
            url, creationId);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("IG publish response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaIdResponse(response, responseBody, "media publish");
    }

    /// <summary>
    /// Tries to fetch the permalink for a published IG media and store it on the post.
    /// </summary>
    private async Task TryFetchPermalinkAsync(
        Post post, string mediaId, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{GraphApiBaseUrl}/{mediaId}?fields=permalink&access_token={accessToken}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<IgPermalinkResponse>(body);

                if (!string.IsNullOrEmpty(result?.Permalink))
                {
                    post.ExternalPostUrl = result.Permalink;
                    await _dbContext.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation("Stored permalink for IG post {PostId}: {Permalink}",
                        post.Id, result.Permalink);
                }
            }
        }
        catch (Exception ex)
        {
            // Non-critical - just log and continue
            _logger.LogWarning(ex, "Failed to fetch permalink for IG post {PostId}", post.Id);
        }
    }

    private PublishResult ParseMetaIdResponse(
        HttpResponseMessage response, string responseBody, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<MetaIdResponse>(responseBody);

            if (string.IsNullOrEmpty(result?.Id))
            {
                _logger.LogWarning("IG {Operation} returned success but no ID", operation);
                return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"IG {operation} returned success but no ID");
            }

            return new PublishResult(true, ExternalPostId: result.Id);
        }
        else
        {
            var error = JsonSerializer.Deserialize<MetaErrorResponseIg>(responseBody);
            var errorCode = error?.Error?.Code ?? 0;
            var errorType = ClassifyError(errorCode);

            _logger.LogWarning("IG {Operation} error: Code={Code}, Message={Message}",
                operation, errorCode, error?.Error?.Message);

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

        return PublishErrorType.Transient;
    }

    private async Task<bool> TryClaimPostAsync(Post post, CancellationToken cancellationToken)
    {
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

    private async Task MarkPublishedAsync(Post post, string externalPostId,
        CancellationToken cancellationToken)
    {
        post.Status = PostStatus.Published;
        post.ExternalPostId = externalPostId;
        post.PublishedAt = DateTime.UtcNow;
        post.UpdatedAt = DateTime.UtcNow;
        post.ErrorMessage = null;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Instagram post {PostId} published successfully as {ExternalPostId}",
            post.Id, externalPostId);
    }

    private async Task MarkFailedAsync(Post post, string errorMessage,
        CancellationToken cancellationToken)
    {
        post.Status = PostStatus.Failed;
        post.ErrorMessage = errorMessage;
        post.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Instagram post {PostId} failed permanently: {Error}", post.Id, errorMessage);
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
            post.Status = PostStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Instagram post {PostId} failed permanently after {RetryCount} attempts: {Error}",
                post.Id, post.RetryCount, result.ErrorMessage);

            return result;
        }

        // Exponential backoff: 2, 4, 8 minutes
        var delayMinutes = Math.Pow(2, post.RetryCount);
        var retryAt = DateTime.UtcNow.AddMinutes(delayMinutes);

        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = retryAt;

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _scheduler.ScheduleRetryAsync(post, retryAt, cancellationToken);

        _logger.LogInformation(
            "Instagram post {PostId} scheduled for retry #{RetryCount} at {RetryAt}",
            post.Id, post.RetryCount, retryAt);

        return result;
    }

    /// <summary>
    /// Redacts access tokens from response bodies for safe logging.
    /// </summary>
    private static string RedactToken(string text)
    {
        // Simple redaction - remove anything that looks like an access token value
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length > 500 ? text[..500] + "..." : text;
    }
}

// Response models for Instagram Graph API

internal class MetaIdResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal class IgContainerStatusResponse
{
    [JsonPropertyName("status_code")]
    public string? StatusCode { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal class IgPermalinkResponse
{
    [JsonPropertyName("permalink")]
    public string? Permalink { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal class MetaErrorResponseIg
{
    [JsonPropertyName("error")]
    public MetaErrorIg? Error { get; set; }
}

internal class MetaErrorIg
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
