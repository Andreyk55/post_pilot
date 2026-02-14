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
/// Publisher implementation for Instagram Feed posts (image + video) using Meta Graph API
/// (Instagram Content Publishing API).
///
/// Image flow (synchronous, same as before):
/// 1. Create image container: POST /{ig-user-id}/media  (image_url + caption)
/// 2. Poll container status: GET /{creation-id}?fields=status_code,status  (usually instant)
/// 3. Publish container: POST /{ig-user-id}/media_publish
/// 4. Fetch permalink
///
/// Video flow (stateful, multi-attempt to avoid long-running execution):
/// A) First attempt (InstagramCreationId is null):
///    - Create video container: POST /{ig-user-id}/media  (media_type=REELS, video_url + caption)
///    - Save InstagramCreationId on the Post
///    - Check container status once; if IN_PROGRESS → schedule short retry (30s)
/// B) Subsequent attempts (InstagramCreationId is set):
///    - Check container status
///    - FINISHED → call media_publish → done
///    - IN_PROGRESS → schedule short retry (don't block, don't count as failure)
///    - ERROR → fail permanently
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

    // Container polling settings (for images - quick in-process polling)
    private const int MaxImagePollAttempts = 30;
    private static readonly TimeSpan ImagePollInterval = TimeSpan.FromSeconds(2);

    // Video processing retry interval (set as NextRetryAt, not in-process wait)
    private static readonly TimeSpan VideoProcessingRetryDelay = TimeSpan.FromSeconds(30);

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

        // Step 5: Route to image or video flow
        try
        {
            PublishResult result;

            if (post.MediaType == MediaType.Video)
            {
                result = await PublishVideoToInstagramAsync(post, accessToken, cancellationToken);

                // Video flow handles its own state transitions for processing retries.
                // If ScheduleProcessingRetryAsync was called, it returns Success=true with no ExternalPostId
                // (the post is in RetryPending, not Published). Only proceed to MarkPublished if we got an ID.
                if (result.Success && string.IsNullOrEmpty(result.ExternalPostId))
                {
                    // Processing retry scheduled — post is already in RetryPending state
                    return result;
                }
            }
            else
            {
                result = await PublishImageToInstagramAsync(post, accessToken, cancellationToken);
            }

            if (result.Success)
            {
                await MarkPublishedAsync(post, result.ExternalPostId!, cancellationToken);

                // Try to fetch permalink
                if (!string.IsNullOrEmpty(result.ExternalPostId))
                {
                    await TryFetchMediaInfoAsync(post, result.ExternalPostId, accessToken, cancellationToken);
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

    // ──────────────────────────────────────────────
    //  IMAGE FLOW (existing, synchronous polling)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Full Instagram image publishing flow (synchronous polling — images process quickly).
    /// </summary>
    private async Task<PublishResult> PublishImageToInstagramAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;

        // Generate a public URL for the image
        var mediaUrl = ResolveMediaUrl(post);

        // Create image container
        var containerResult = await CreateImageContainerAsync(
            igUserId, mediaUrl, post.Content, accessToken, cancellationToken);

        if (!containerResult.Success)
            return containerResult;

        var creationId = containerResult.ExternalPostId!;
        _logger.LogInformation("Created IG image container {CreationId} for post {PostId}",
            creationId, post.Id);

        // Poll for container to be ready (images are fast)
        var pollResult = await PollContainerStatusInProcessAsync(
            creationId, accessToken, MaxImagePollAttempts, ImagePollInterval, cancellationToken);

        if (!pollResult.Success)
            return pollResult;

        // Publish the container
        return await PublishMediaContainerAsync(igUserId, creationId, accessToken, cancellationToken);
    }

    // ──────────────────────────────────────────────
    //  VIDEO FLOW (stateful, multi-attempt)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Stateful Instagram video publishing flow.
    /// - If no container exists yet: create it, check status once, schedule retry if IN_PROGRESS.
    /// - If container exists: check status, publish if FINISHED, schedule retry if IN_PROGRESS.
    /// Never blocks for long — returns quickly and uses NextRetryAt for processing waits.
    /// </summary>
    private async Task<PublishResult> PublishVideoToInstagramAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;

        // Step A: Create container if we don't have one yet
        if (string.IsNullOrEmpty(post.InstagramCreationId))
        {
            var mediaUrl = ResolveMediaUrl(post);

            var containerResult = await CreateVideoContainerAsync(
                igUserId, mediaUrl, post.Content, accessToken, cancellationToken);

            if (!containerResult.Success)
                return containerResult;

            var creationId = containerResult.ExternalPostId!;

            // Persist the container ID so we can resume on next attempt
            post.InstagramCreationId = creationId;
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Created IG video container {CreationId} for post {PostId}, checking status...",
                creationId, post.Id);
        }

        // Step B: Check container status (single check, no in-process loop)
        var statusResult = await CheckContainerStatusAsync(
            post.InstagramCreationId!, accessToken, cancellationToken);

        switch (statusResult.Status)
        {
            case IgContainerStatus.Finished:
                _logger.LogInformation(
                    "IG video container {CreationId} is FINISHED, publishing for post {PostId}",
                    post.InstagramCreationId, post.Id);

                return await PublishMediaContainerAsync(
                    igUserId, post.InstagramCreationId!, accessToken, cancellationToken);

            case IgContainerStatus.InProgress:
                // Video still processing — schedule a short retry without counting as a failure
                return await ScheduleProcessingRetryAsync(post, cancellationToken);

            case IgContainerStatus.Error:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Video container processing failed: {statusResult.ErrorMessage}");

            case IgContainerStatus.Expired:
                // Container expired; clear it so a fresh container can be created on retry
                post.InstagramCreationId = null;
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Video container expired before publishing");

            default:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Unknown container status: {statusResult.Status}");
        }
    }

    /// <summary>
    /// Schedules a short retry for video processing without counting it as a hard failure.
    /// Uses ProcessingPollCount (separate from RetryCount) with its own limit.
    /// </summary>
    private async Task<PublishResult> ScheduleProcessingRetryAsync(
        Post post, CancellationToken cancellationToken)
    {
        post.ProcessingPollCount++;

        if (post.ProcessingPollCount >= Post.MaxProcessingPollCount)
        {
            _logger.LogWarning(
                "IG video post {PostId} exceeded max processing polls ({Max}), failing with timeout",
                post.Id, Post.MaxProcessingPollCount);

            post.Status = PostStatus.Failed;
            post.ErrorMessage = $"Video processing timed out after {post.ProcessingPollCount} status checks (~{post.ProcessingPollCount * 30}s)";
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new PublishResult(false,
                ErrorType: PublishErrorType.Permanent,
                ErrorMessage: post.ErrorMessage);
        }

        var retryAt = DateTime.UtcNow.Add(VideoProcessingRetryDelay);

        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = retryAt;
        post.ErrorMessage = $"Video processing in progress (poll {post.ProcessingPollCount}/{Post.MaxProcessingPollCount})";
        post.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        await _scheduler.ScheduleRetryAsync(post, retryAt, cancellationToken);

        _logger.LogInformation(
            "IG video post {PostId} still processing, scheduled retry #{PollCount} at {RetryAt}",
            post.Id, post.ProcessingPollCount, retryAt);

        // Return success=false but NOT a hard failure — the caller won't call HandlePublishFailureAsync
        // because we already handled the state transition here.
        return new PublishResult(true);
    }

    // ──────────────────────────────────────────────
    //  GRAPH API METHODS
    // ──────────────────────────────────────────────

    /// <summary>
    /// Resolves a public URL for the post's media (generates pre-signed URL if S3 key).
    /// </summary>
    private string ResolveMediaUrl(Post post)
    {
        if (_mediaService.IsS3Key(post.MediaUrl!))
        {
            var url = _mediaService.GenerateDownloadUrl(post.MediaUrl!, MediaDownloadUrlExpiration);
            _logger.LogInformation("Generated pre-signed URL for S3 key {S3Key} for IG post {PostId}",
                post.MediaUrl, post.Id);
            return url;
        }
        return post.MediaUrl!;
    }

    /// <summary>
    /// Resolves the page access token for an Instagram Business Account
    /// by looking up the linked Facebook Page.
    /// </summary>
    private async Task<string?> ResolveAccessTokenAsync(
        ConnectedInstagramAccount igAccount, CancellationToken cancellationToken)
    {
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
    /// POST /{ig-user-id}/media with image_url and caption (image container).
    /// </summary>
    private async Task<PublishResult> CreateImageContainerAsync(
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

        _logger.LogInformation("Creating IG image container: POST {Url}", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("IG image container response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaIdResponse(response, responseBody, "image container creation");
    }

    /// <summary>
    /// POST /{ig-user-id}/media with media_type=REELS, video_url, and caption (video container).
    /// </summary>
    private async Task<PublishResult> CreateVideoContainerAsync(
        string igUserId, string videoUrl, string caption,
        string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{GraphApiBaseUrl}/{igUserId}/media";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["media_type"] = "REELS",
            ["video_url"] = videoUrl,
            ["caption"] = caption ?? "",
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("Creating IG video container: POST {Url}", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("IG video container response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaIdResponse(response, responseBody, "video container creation");
    }

    /// <summary>
    /// Single container status check (no looping). Returns parsed status.
    /// Used by the video flow for non-blocking status checks.
    /// </summary>
    private async Task<ContainerStatusResult> CheckContainerStatusAsync(
        string creationId, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{GraphApiBaseUrl}/{creationId}?fields=status_code,status&access_token={accessToken}";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("IG container status check failed: {StatusCode} - {Body}",
                response.StatusCode, RedactToken(responseBody));

            // Treat HTTP errors on status check as transient (may recover on next poll)
            return new ContainerStatusResult(IgContainerStatus.Unknown,
                $"Status check HTTP error: {response.StatusCode}");
        }

        var statusResult = JsonSerializer.Deserialize<IgContainerStatusResponse>(responseBody);
        var statusCode = statusResult?.StatusCode?.ToUpperInvariant();

        _logger.LogInformation("IG container {CreationId} status: {StatusCode}",
            creationId, statusCode);

        return statusCode switch
        {
            "FINISHED" => new ContainerStatusResult(IgContainerStatus.Finished),
            "ERROR" => new ContainerStatusResult(IgContainerStatus.Error,
                statusResult?.Status ?? "Container processing failed"),
            "EXPIRED" => new ContainerStatusResult(IgContainerStatus.Expired,
                "Container expired before publishing"),
            "IN_PROGRESS" or null or "" => new ContainerStatusResult(IgContainerStatus.InProgress),
            _ => new ContainerStatusResult(IgContainerStatus.Unknown, $"Unknown status: {statusCode}"),
        };
    }

    /// <summary>
    /// Polls container status in-process (used for images which process quickly).
    /// </summary>
    private async Task<PublishResult> PollContainerStatusInProcessAsync(
        string creationId, string accessToken,
        int maxAttempts, TimeSpan interval, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await Task.Delay(interval, cancellationToken);

            var result = await CheckContainerStatusAsync(creationId, accessToken, cancellationToken);

            _logger.LogInformation("IG container {CreationId} poll attempt {Attempt}/{Max}: {Status}",
                creationId, attempt + 1, maxAttempts, result.Status);

            switch (result.Status)
            {
                case IgContainerStatus.Finished:
                    return new PublishResult(true, ExternalPostId: creationId);

                case IgContainerStatus.Error:
                    return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                        ErrorMessage: $"Container processing failed: {result.ErrorMessage}");

                case IgContainerStatus.Expired:
                    return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                        ErrorMessage: "Container expired before publishing");

                case IgContainerStatus.InProgress:
                case IgContainerStatus.Unknown:
                default:
                    if (attempt >= 3 && result.Status == IgContainerStatus.Unknown)
                    {
                        return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                            ErrorMessage: $"Container status check failed after {attempt + 1} attempts");
                    }
                    break;
            }
        }

        return new PublishResult(false, ErrorType: PublishErrorType.Transient,
            ErrorMessage: $"Container not ready after {maxAttempts} poll attempts (timeout)");
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
    /// Fetches media info (permalink + media_type) for a published IG media and stores both on the post.
    /// Falls back to deriving media type from permalink URL if the Graph API call fails.
    /// </summary>
    private async Task TryFetchMediaInfoAsync(
        Post post, string mediaId, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{GraphApiBaseUrl}/{mediaId}?fields=permalink,media_type&access_token={accessToken}";
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var result = JsonSerializer.Deserialize<IgMediaInfoResponse>(body);

                if (!string.IsNullOrEmpty(result?.Permalink))
                {
                    post.ExternalPostUrl = result.Permalink;
                }

                if (!string.IsNullOrEmpty(result?.MediaType))
                {
                    post.InstagramMediaType = ParseGraphMediaType(result.MediaType);

                    _logger.LogInformation(
                        "Stored IG media info for post {PostId}: permalink={Permalink}, media_type={MediaType}",
                        post.Id, result.Permalink, result.MediaType);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                return;
            }

            _logger.LogWarning(
                "IG media info fetch failed for post {PostId}: HTTP {StatusCode}",
                post.Id, (int)response.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch media info for IG post {PostId}, using fallback", post.Id);
        }

        // Fallback: derive media type from permalink URL if we have one
        TrySetMediaTypeFromPermalink(post);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Maps the Graph API media_type string to our InstagramMediaType enum.
    /// </summary>
    internal static Enums.InstagramMediaType ParseGraphMediaType(string graphMediaType)
    {
        return graphMediaType?.ToUpperInvariant() switch
        {
            "IMAGE" => Enums.InstagramMediaType.Image,
            "VIDEO" => Enums.InstagramMediaType.Reels, // IG API returns VIDEO for Reels
            "REELS" => Enums.InstagramMediaType.Reels,
            "CAROUSEL_ALBUM" => Enums.InstagramMediaType.CarouselAlbum,
            _ => Enums.InstagramMediaType.Unknown,
        };
    }

    /// <summary>
    /// Fallback: derive IG media type from the permalink URL pattern.
    /// Only used when the Graph API media info call fails.
    /// </summary>
    private void TrySetMediaTypeFromPermalink(Post post)
    {
        if (string.IsNullOrEmpty(post.ExternalPostUrl))
            return;

        var url = post.ExternalPostUrl;
        if (url.Contains("/reel/", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/reels/", StringComparison.OrdinalIgnoreCase))
        {
            post.InstagramMediaType = Enums.InstagramMediaType.Reels;
            _logger.LogInformation(
                "Fallback: derived IG media type REELS from permalink for post {PostId}", post.Id);
        }
        else if (url.Contains("/p/", StringComparison.OrdinalIgnoreCase))
        {
            post.InstagramMediaType = Enums.InstagramMediaType.Image;
            _logger.LogInformation(
                "Fallback: derived IG media type IMAGE from permalink for post {PostId}", post.Id);
        }
        else
        {
            post.InstagramMediaType = Enums.InstagramMediaType.Unknown;
            _logger.LogWarning(
                "Fallback: could not derive IG media type from permalink for post {PostId}: {Url}",
                post.Id, url);
        }
    }

    // ──────────────────────────────────────────────
    //  HELPERS
    // ──────────────────────────────────────────────

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
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length > 500 ? text[..500] + "..." : text;
    }
}

// ──────────────────────────────────────────────
//  INTERNAL TYPES
// ──────────────────────────────────────────────

/// <summary>
/// Parsed container status from the IG Graph API.
/// </summary>
internal enum IgContainerStatus
{
    Finished,
    InProgress,
    Error,
    Expired,
    Unknown,
}

/// <summary>
/// Result of a single container status check.
/// </summary>
internal record ContainerStatusResult(
    IgContainerStatus Status,
    string? ErrorMessage = null
);

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

internal class IgMediaInfoResponse
{
    [JsonPropertyName("permalink")]
    public string? Permalink { get; set; }

    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

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
