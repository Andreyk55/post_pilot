using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Publisher for Instagram Stories using Meta Graph API.
///
/// Image story flow (synchronous polling — images process quickly):
/// 1. Create container: POST /{ig-user-id}/media (media_type=STORIES, image_url)
/// 2. Poll container status until FINISHED
/// 3. Publish container: POST /{ig-user-id}/media_publish
///
/// Video story flow (stateful, multi-attempt — same pattern as feed video):
/// A) First attempt (InstagramCreationId is null):
///    - Create container: POST /{ig-user-id}/media (media_type=STORIES, video_url)
///    - Save InstagramCreationId
///    - Check status once; if IN_PROGRESS → schedule 30s retry
/// B) Subsequent attempts (InstagramCreationId is set):
///    - Check status → FINISHED → publish; IN_PROGRESS → retry; ERROR → fail
///
/// Note: Instagram stories do NOT support captions via the API.
/// </summary>
public class InstagramStoryPublisher : IStoryPublisher
{
    private readonly AppDbContext _dbContext;
    private readonly IPostScheduler _scheduler;
    private readonly IMediaService _mediaService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<InstagramStoryPublisher> _logger;
    private readonly string _graphApiBaseUrl;
    private readonly TimeSpan _mediaDownloadUrlExpiration;
    private readonly int _maxImagePollAttempts;
    private readonly TimeSpan _imagePollInterval;

    // Video processing retry interval
    /// <summary>
    /// Computes progressive polling delay based on poll count.
    /// Polls 1–4: 30s, 5–10: 60s, 11–15: 120s, 16–20: 180s.
    /// </summary>
    private static int GetProcessingPollDelaySeconds(int pollCount)
    {
        return pollCount switch
        {
            <= 4  => 30,
            <= 10 => 60,
            <= 15 => 120,
            _     => 180,
        };
    }

    // Meta error codes — transient (retry)
    private static readonly HashSet<int> TransientErrorCodes = new()
    {
        1,    // Unknown error
        2,    // Service temporarily unavailable
        4,    // Too many calls
        17,   // User request limit reached
        341,  // Temporarily blocked
        368,  // Temporarily blocked for policies violation
    };

    // Meta error codes — permanent (don't retry)
    private static readonly HashSet<int> PermanentErrorCodes = new()
    {
        10,    // Permission denied
        100,   // Invalid parameter
        102,   // Session invalidated
        190,   // Access token expired or invalid
        200,   // Permission error
        220,   // Application does not have permission
        230,   // Incorrect permission
        250,   // Insufficient permission
        270,   // Permission revoked
        294,   // App not installed
        36003, // IG media creation failed
    };

    public Platform SupportedPlatform => Platform.Instagram;

    public InstagramStoryPublisher(
        AppDbContext dbContext,
        IPostScheduler scheduler,
        IMediaService mediaService,
        HttpClient httpClient,
        ILogger<InstagramStoryPublisher> logger,
        MetaApiOptions metaApiOptions,
        PublishingOptions publishingOptions)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _mediaService = mediaService;
        _httpClient = httpClient;
        _logger = logger;
        _graphApiBaseUrl = metaApiOptions.GraphApiBaseUrl;
        _mediaDownloadUrlExpiration = TimeSpan.FromMinutes(publishingOptions.MediaDownloadUrlExpirationMinutes);
        _maxImagePollAttempts = publishingOptions.ImagePollMaxAttempts;
        _imagePollInterval = TimeSpan.FromSeconds(publishingOptions.ImagePollIntervalSeconds);
    }

    public async Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Instagram story publish for post {PostId}", postId);

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
            _logger.LogInformation("Story {PostId} already published as {ExternalPostId}",
                postId, post.ExternalPostId);
            return new PublishResult(true, ExternalPostId: post.ExternalPostId,
                ErrorType: PublishErrorType.AlreadyPublished);
        }

        // Step 3: Atomically claim the post
        var claimResult = await TryClaimPostAsync(post, cancellationToken);
        if (!claimResult)
        {
            _logger.LogInformation("Story {PostId} already being processed by another worker", postId);
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

        var accessToken = await ResolveAccessTokenAsync(post.TargetInstagramAccount, cancellationToken);
        if (string.IsNullOrEmpty(accessToken))
        {
            await MarkFailedAsync(post, "No access token available for the linked Facebook Page", cancellationToken);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "No access token for linked Facebook Page");
        }

        // Step 5: Last-moment cancellation check
        var currentStatus = await _dbContext.Posts
            .Where(p => p.Id == postId)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (currentStatus == PostStatus.Canceled)
        {
            _logger.LogInformation("Story {PostId} was canceled before Meta API call, aborting", postId);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Post was canceled");
        }

        // Step 6: Route to image or video story flow
        try
        {
            PublishResult result;

            if (post.MediaType == MediaType.Video)
            {
                result = await PublishVideoStoryAsync(post, accessToken, cancellationToken);

                // Video flow handles its own state for processing retries
                if (result.Success && string.IsNullOrEmpty(result.ExternalPostId))
                    return result;
            }
            else
            {
                result = await PublishImageStoryAsync(post, accessToken, cancellationToken);
            }

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
            _logger.LogError(ex, "Network error publishing Instagram story {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Network error: {ex.Message}"),
                cancellationToken);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout publishing Instagram story {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Request timed out"),
                cancellationToken);
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            _logger.LogError(ex, "Transient error publishing Instagram story {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Transient error: {ex.Message}"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Internal error (non-retryable) publishing Instagram story {PostId}: {ExceptionType}", postId, ex.GetType().Name);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Internal error (non-retryable): {ex.GetType().Name}: {ex.Message}"),
                cancellationToken);
        }
    }

    // ──────────────────────────────────────────────
    //  IMAGE STORY FLOW
    // ──────────────────────────────────────────────

    private async Task<PublishResult> PublishImageStoryAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;
        var mediaUrl = await ResolveMediaUrlAsync(post, cancellationToken);

        // Create story container with media_type=STORIES
        var containerResult = await CreateStoryContainerAsync(
            igUserId, mediaUrl, null, "image_url", accessToken, cancellationToken);

        if (!containerResult.Success)
            return containerResult;

        var creationId = containerResult.ExternalPostId!;
        _logger.LogInformation("Created IG story image container {CreationId} for post {PostId}",
            creationId, post.Id);

        // Poll for container to be ready (images are fast)
        var pollResult = await PollContainerStatusInProcessAsync(
            creationId, accessToken, _maxImagePollAttempts, _imagePollInterval, cancellationToken);

        if (!pollResult.Success)
            return pollResult;

        // Publish the container
        return await PublishMediaContainerAsync(igUserId, creationId, accessToken, cancellationToken);
    }

    // ──────────────────────────────────────────────
    //  VIDEO STORY FLOW (stateful, non-blocking)
    // ──────────────────────────────────────────────

    private async Task<PublishResult> PublishVideoStoryAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;

        // Step A: Create container if we don't have one yet
        if (string.IsNullOrEmpty(post.InstagramCreationId))
        {
            var mediaUrl = await ResolveMediaUrlAsync(post, cancellationToken);

            var containerResult = await CreateStoryContainerAsync(
                igUserId, mediaUrl, null, "video_url", accessToken, cancellationToken);

            if (!containerResult.Success)
                return containerResult;

            post.InstagramCreationId = containerResult.ExternalPostId!;
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Created IG story video container {CreationId} for post {PostId}",
                post.InstagramCreationId, post.Id);
        }

        // Step B: Check container status (single check)
        var statusResult = await CheckContainerStatusAsync(
            post.InstagramCreationId!, accessToken, cancellationToken);

        switch (statusResult.Status)
        {
            case IgStoryContainerStatus.Finished:
                _logger.LogInformation(
                    "IG story video container {CreationId} is FINISHED, publishing for post {PostId}",
                    post.InstagramCreationId, post.Id);

                return await PublishMediaContainerAsync(
                    igUserId, post.InstagramCreationId!, accessToken, cancellationToken);

            case IgStoryContainerStatus.InProgress:
                return await ScheduleProcessingRetryAsync(post, cancellationToken);

            case IgStoryContainerStatus.Error:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Story video container processing failed: {statusResult.ErrorMessage}");

            case IgStoryContainerStatus.Expired:
                post.InstagramCreationId = null;
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Story video container expired before publishing");

            default:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Unknown container status: {statusResult.Status}");
        }
    }

    private async Task<PublishResult> ScheduleProcessingRetryAsync(
        Post post, CancellationToken cancellationToken)
    {
        var freshStatus = await _dbContext.Posts
            .Where(p => p.Id == post.Id)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (freshStatus == PostStatus.Canceled)
        {
            _logger.LogInformation("Story {PostId} was canceled, skipping processing retry", post.Id);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Post was canceled");
        }

        post.ProcessingPollCount++;

        if (post.ProcessingPollCount >= Post.MaxProcessingPollCount)
        {
            _logger.LogWarning(
                "IG story video {PostId} exceeded max processing polls ({Max}), failing with timeout",
                post.Id, Post.MaxProcessingPollCount);

            post.Status = PostStatus.Failed;
            post.ErrorMessage = $"Video processing timed out after {post.ProcessingPollCount} status checks";
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new PublishResult(false,
                ErrorType: PublishErrorType.Permanent,
                ErrorMessage: post.ErrorMessage);
        }

        var delaySeconds = GetProcessingPollDelaySeconds(post.ProcessingPollCount);
        var retryAt = DateTime.UtcNow.AddSeconds(delaySeconds);

        post.Status = PostStatus.Processing;
        post.NextRetryAt = retryAt;
        post.ErrorMessage = $"Processing\u2026";
        post.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _scheduler.ScheduleRetryAsync(post, retryAt, cancellationToken);

        _logger.LogInformation(
            "Processing poll scheduled: poll={PollCount}/{MaxPoll} next={RetryAt} delay={DelaySeconds}s PostId={PostId}",
            post.ProcessingPollCount, Post.MaxProcessingPollCount, retryAt, delaySeconds, post.Id);

        return new PublishResult(true);
    }

    // ──────────────────────────────────────────────
    //  GRAPH API METHODS
    // ──────────────────────────────────────────────

    /// <summary>
    /// Creates a story container: POST /{ig-user-id}/media with media_type=STORIES.
    /// </summary>
    private async Task<PublishResult> CreateStoryContainerAsync(
        string igUserId, string mediaUrl, string? caption,
        string mediaUrlParam, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{igUserId}/media";
        var parameters = new Dictionary<string, string>
        {
            ["media_type"] = "STORIES",
            [mediaUrlParam] = mediaUrl,
            ["access_token"] = accessToken,
        };

        // IG stories don't support captions via API, but we include it
        // in case Meta adds support in the future (it will just be ignored).
        // For now, intentionally NOT sending caption for stories.

        var content = new FormUrlEncodedContent(parameters);

        _logger.LogInformation("Creating IG story container: POST {Url} (media_type=STORIES)", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("IG story container response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaIdResponse(response, responseBody, "story container creation");
    }

    private async Task<StoryContainerStatusResult> CheckContainerStatusAsync(
        string creationId, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{creationId}?fields=status_code,status&access_token={accessToken}";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("IG story container status check failed: {StatusCode} - {Body}",
                response.StatusCode, RedactToken(responseBody));

            return new StoryContainerStatusResult(IgStoryContainerStatus.Unknown,
                $"Status check HTTP error: {response.StatusCode}");
        }

        var statusResult = JsonSerializer.Deserialize<IgContainerStatusResponseLocal>(responseBody);
        var statusCode = statusResult?.StatusCode?.ToUpperInvariant();

        _logger.LogInformation("IG story container {CreationId} status: {StatusCode}",
            creationId, statusCode);

        return statusCode switch
        {
            "FINISHED" => new StoryContainerStatusResult(IgStoryContainerStatus.Finished),
            "ERROR" => new StoryContainerStatusResult(IgStoryContainerStatus.Error,
                statusResult?.Status ?? "Container processing failed"),
            "EXPIRED" => new StoryContainerStatusResult(IgStoryContainerStatus.Expired,
                "Container expired before publishing"),
            "IN_PROGRESS" or null or "" => new StoryContainerStatusResult(IgStoryContainerStatus.InProgress),
            _ => new StoryContainerStatusResult(IgStoryContainerStatus.Unknown, $"Unknown status: {statusCode}"),
        };
    }

    private async Task<PublishResult> PollContainerStatusInProcessAsync(
        string creationId, string accessToken,
        int maxAttempts, TimeSpan interval, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await Task.Delay(interval, cancellationToken);

            var result = await CheckContainerStatusAsync(creationId, accessToken, cancellationToken);

            _logger.LogInformation("IG story container {CreationId} poll attempt {Attempt}/{Max}: {Status}",
                creationId, attempt + 1, maxAttempts, result.Status);

            switch (result.Status)
            {
                case IgStoryContainerStatus.Finished:
                    return new PublishResult(true, ExternalPostId: creationId);

                case IgStoryContainerStatus.Error:
                    return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                        ErrorMessage: $"Story container processing failed: {result.ErrorMessage}");

                case IgStoryContainerStatus.Expired:
                    return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                        ErrorMessage: "Story container expired before publishing");

                case IgStoryContainerStatus.InProgress:
                case IgStoryContainerStatus.Unknown:
                default:
                    if (attempt >= 3 && result.Status == IgStoryContainerStatus.Unknown)
                    {
                        return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                            ErrorMessage: $"Story container status check failed after {attempt + 1} attempts");
                    }
                    break;
            }
        }

        return new PublishResult(false, ErrorType: PublishErrorType.Transient,
            ErrorMessage: $"Story container not ready after {maxAttempts} poll attempts (timeout)");
    }

    private async Task<PublishResult> PublishMediaContainerAsync(
        string igUserId, string creationId,
        string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{igUserId}/media_publish";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["creation_id"] = creationId,
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("Publishing IG story container: POST {Url} creation_id={CreationId}",
            url, creationId);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("IG story publish response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaIdResponse(response, responseBody, "story media publish");
    }

    // ──────────────────────────────────────────────
    //  HELPERS
    // ──────────────────────────────────────────────

    private async Task<string> ResolveMediaUrlAsync(Post post, CancellationToken cancellationToken)
    {
        if (_mediaService.IsStorageKey(post.MediaUrl!))
        {
            var url = await _mediaService.GetPublishingUrlAsync(post.MediaUrl!, _mediaDownloadUrlExpiration, cancellationToken);
            _logger.LogInformation("Generated publishing URL for storage key {StorageKey} for IG story {PostId}",
                post.MediaUrl, post.Id);
            return url;
        }
        return post.MediaUrl!;
    }

    /// <summary>
    /// Resolves the page access token for an Instagram story by looking up the
    /// linked Facebook Page. Filtered by WorkspaceId so two workspaces holding
    /// the same external PageId (agency case) never cross over.
    /// </summary>
    private async Task<string?> ResolveAccessTokenAsync(
        ConnectedInstagramAccount igAccount, CancellationToken cancellationToken)
    {
        var connectedPage = await _dbContext.Set<ConnectedPage>()
            .FirstOrDefaultAsync(
                p => p.PageId == igAccount.PageId && p.WorkspaceId == igAccount.WorkspaceId,
                cancellationToken);

        if (connectedPage == null)
        {
            _logger.LogWarning(
                "No ConnectedPage found for Facebook PageId {PageId} linked to IG account {IgAccountId} in workspace {WorkspaceId}",
                igAccount.PageId, igAccount.Id, igAccount.WorkspaceId);
            return null;
        }

        return connectedPage.AccessToken;
    }

    private PublishResult ParseMetaIdResponse(
        HttpResponseMessage response, string responseBody, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<MetaIdResponseLocal>(responseBody);

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
            var error = JsonSerializer.Deserialize<MetaErrorResponseLocal>(responseBody);
            var errorCode = error?.Error?.Code ?? 0;
            var errorType = ClassifyError(errorCode, error?.Error?.ErrorSubcode, error?.Error?.FbTraceId, error?.Error?.Message);

            _logger.LogWarning("IG {Operation} error: Code={Code}, Subcode={Subcode}, Message={Message}, FbTraceId={FbTraceId}",
                operation, errorCode, error?.Error?.ErrorSubcode, error?.Error?.Message, error?.Error?.FbTraceId);

            return new PublishResult(false,
                ErrorType: errorType,
                ErrorMessage: error?.Error?.Message ?? $"HTTP {(int)response.StatusCode}");
        }
    }

    private PublishErrorType ClassifyError(int errorCode, int? subcode = null, string? fbTraceId = null, string? message = null)
    {
        if (PermanentErrorCodes.Contains(errorCode))
            return PublishErrorType.Permanent;

        if (TransientErrorCodes.Contains(errorCode))
            return PublishErrorType.Transient;

        // Unknown code — default to transient but log details for investigation
        _logger.LogWarning(
            "Unknown Meta API error code defaulting to Transient: Code={Code} Subcode={Subcode} FbTraceId={FbTraceId} Message={Message}",
            errorCode, subcode, fbTraceId, message);
        return PublishErrorType.Transient;
    }

    /// <summary>
    /// Returns true for exceptions that represent transient failures (network, timeout).
    /// Programming bugs (NullReference, Argument, etc.) return false → permanent failure.
    /// </summary>
    private static bool IsTransientException(Exception ex)
    {
        return ex is HttpRequestException
            or TimeoutException
            or TaskCanceledException
            or OperationCanceledException;
    }

    private async Task<bool> TryClaimPostAsync(Post post, CancellationToken cancellationToken)
    {
        var rowsAffected = await _dbContext.Posts
            .Where(p => p.Id == post.Id &&
                       (p.Status == PostStatus.Scheduled || p.Status == PostStatus.RetryPending || p.Status == PostStatus.Processing))
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

        _logger.LogInformation("Instagram story {PostId} published successfully as {ExternalPostId}",
            post.Id, externalPostId);
    }

    private async Task MarkFailedAsync(Post post, string errorMessage,
        CancellationToken cancellationToken)
    {
        post.Status = PostStatus.Failed;
        post.ErrorMessage = errorMessage;
        post.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Instagram story {PostId} failed permanently: {Error}", post.Id, errorMessage);
    }

    private async Task<PublishResult> HandlePublishFailureAsync(
        Post post, PublishResult result, CancellationToken cancellationToken)
    {
        var freshStatus = await _dbContext.Posts
            .Where(p => p.Id == post.Id)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (freshStatus == PostStatus.Canceled)
        {
            _logger.LogInformation("Story {PostId} was canceled, skipping retry", post.Id);
            return result;
        }

        post.RetryCount++;
        post.ErrorMessage = result.ErrorMessage;
        post.UpdatedAt = DateTime.UtcNow;

        if (result.ErrorType == PublishErrorType.Permanent || post.RetryCount >= post.MaxRetries)
        {
            post.Status = PostStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Instagram story {PostId} failed permanently after {RetryCount} attempts: {Error}",
                post.Id, post.RetryCount, result.ErrorMessage);

            return result;
        }

        var delayMinutes = Math.Pow(2, post.RetryCount);
        var retryAt = DateTime.UtcNow.AddMinutes(delayMinutes);

        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = retryAt;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _scheduler.ScheduleRetryAsync(post, retryAt, cancellationToken);

        _logger.LogInformation(
            "Transient failure retry scheduled (attempt {RetryCount}/{MaxRetries}) NextRetryAt={RetryAt} PostId={PostId}",
            post.RetryCount, post.MaxRetries, retryAt, post.Id);

        return result;
    }

    private static string RedactToken(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length > 500 ? text[..500] + "..." : text;
    }
}

// Internal types for Instagram story publisher (avoid conflicts with other publisher's types)

internal enum IgStoryContainerStatus
{
    Finished,
    InProgress,
    Error,
    Expired,
    Unknown,
}

internal record StoryContainerStatusResult(
    IgStoryContainerStatus Status,
    string? ErrorMessage = null
);

internal class MetaIdResponseLocal
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal class IgContainerStatusResponseLocal
{
    [JsonPropertyName("status_code")]
    public string? StatusCode { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal class MetaErrorResponseLocal
{
    [JsonPropertyName("error")]
    public MetaErrorLocal? Error { get; set; }
}

internal class MetaErrorLocal
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
