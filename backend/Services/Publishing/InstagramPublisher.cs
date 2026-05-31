using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;

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
    private readonly Providers.IProviderConnectionService _providerConnections;
    private readonly string _graphApiBaseUrl;
    private readonly TimeSpan _mediaDownloadUrlExpiration;
    private readonly TimeSpan _videoDownloadUrlExpiration;
    private readonly int _maxImagePollAttempts;
    private readonly TimeSpan _imagePollInterval;

    // Video processing retry interval (set as NextRetryAt, not in-process wait)
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

    // Meta error codes - auth/token problems. Do NOT retry; additionally flag the
    // workspace connection ReauthRequired (without disconnecting or canceling posts).
    private static readonly HashSet<int> AuthErrorCodes = new()
    {
        10,   // Permission denied
        102,  // Session invalidated
        190,  // Access token expired or invalid
        200,  // Permission error
        220,  // Application does not have permission
        230,  // Incorrect permission
        250,  // Insufficient permission
        270,  // Permission revoked
        463,  // Access token expired
        467,  // Access token invalid
    };

    // Meta error codes - permanent (don't retry, not an auth problem)
    private static readonly HashSet<int> PermanentErrorCodes = new()
    {
        100,  // Invalid parameter
        294,  // App not installed
        36003, // IG media creation failed
    };

    public Platform SupportedPlatform => Platform.Instagram;

    public InstagramPublisher(
        AppDbContext dbContext,
        IPostScheduler scheduler,
        IMediaService mediaService,
        HttpClient httpClient,
        ILogger<InstagramPublisher> logger,
        Providers.IProviderConnectionService providerConnections,
        MetaApiOptions metaApiOptions,
        PublishingOptions publishingOptions)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _mediaService = mediaService;
        _httpClient = httpClient;
        _logger = logger;
        _providerConnections = providerConnections;
        _graphApiBaseUrl = metaApiOptions.GraphApiBaseUrl;
        _mediaDownloadUrlExpiration = TimeSpan.FromMinutes(publishingOptions.MediaDownloadUrlExpirationMinutes);
        _videoDownloadUrlExpiration = TimeSpan.FromMinutes(publishingOptions.VideoDownloadUrlExpirationMinutes);
        _maxImagePollAttempts = publishingOptions.ImagePollMaxAttempts;
        _imagePollInterval = TimeSpan.FromSeconds(publishingOptions.ImagePollIntervalSeconds);
    }

    public async Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(PostPilotLogEvents.PublishStart, "IG_PUBLISH_START postId={PostId}", postId);

        // Step 1: Load post with target IG account (+ its parent connection) and media items
        var post = await _dbContext.Posts
            .Include(p => p.TargetInstagramAccount)
                .ThenInclude(ig => ig!.MetaConnection)
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == postId, cancellationToken);

        if (post == null)
        {
            _logger.LogWarning("Post {PostId} not found", postId);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Post not found");
        }

        // Establish per-publish scope so all subsequent logs include PostId, Platform, AccountId
        using var publishScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["PostId"]    = postId,
            ["Platform"]  = "Instagram",
            ["AccountId"] = post.TargetInstagramAccount?.IgBusinessId ?? string.Empty
        });

        // Step 2: Idempotency check
        if (post.Status == PostStatus.Published && !string.IsNullOrEmpty(post.ExternalPostId))
        {
            _logger.LogInformation("Post {PostId} already published as {ExternalPostId}",
                postId, post.ExternalPostId);
            return new PublishResult(true, ExternalPostId: post.ExternalPostId,
                ErrorType: PublishErrorType.AlreadyPublished);
        }

        // Step 2.5: PUBLISH GATE — refuse to publish while the connection is
        // ReauthRequired (ownership held, token invalid). Checked BEFORE claiming so
        // the post isn't stranded in Publishing and is NOT retried.
        if (PublishGate.IsReauthRequired(post.TargetInstagramAccount, post.TargetInstagramAccount?.MetaConnection))
        {
            _logger.LogWarning(
                "IG_PUBLISH_BLOCKED_REAUTH postId={PostId} — connection needs reauthorization; not publishing.",
                postId);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Publishing is paused because the Meta connection needs to be reauthorized. Reconnect to resume.");
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

        // Step 5: Last-moment safety check — reload status to catch cancellations
        var currentStatus = await _dbContext.Posts
            .Where(p => p.Id == postId)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (currentStatus == PostStatus.Canceled)
        {
            _logger.LogInformation("Post {PostId} was canceled before Meta API call, aborting publish", postId);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Post was canceled");
        }

        // Step 6: Route to image, video, carousel (images), carousel (videos), or mixed carousel flow
        var isCarousel = post.MediaItems?.Count >= 2;
        var isVideoCarousel = isCarousel && post.MediaItems!.All(m => m.MediaType == Enums.MediaType.Video);
        var isImageCarousel = isCarousel && post.MediaItems!.All(m => m.MediaType == Enums.MediaType.Image);
        var isMixedCarousel = isCarousel && !isVideoCarousel && !isImageCarousel;
        try
        {
            PublishResult result;

            if (isMixedCarousel)
            {
                result = await PublishMixedCarouselToInstagramAsync(post, accessToken, cancellationToken);

                // Mixed carousel flow handles its own state transitions for processing retries.
                if (result.Success && string.IsNullOrEmpty(result.ExternalPostId))
                {
                    return result;
                }
            }
            else if (isVideoCarousel)
            {
                result = await PublishVideoCarouselToInstagramAsync(post, accessToken, cancellationToken);

                // Video carousel flow handles its own state transitions for processing retries.
                if (result.Success && string.IsNullOrEmpty(result.ExternalPostId))
                {
                    return result;
                }
            }
            else if (isCarousel)
            {
                result = await PublishCarouselToInstagramAsync(post, accessToken, cancellationToken);

                // Carousel flow handles its own state transitions for processing retries.
                if (result.Success && string.IsNullOrEmpty(result.ExternalPostId))
                {
                    return result;
                }
            }
            else if (post.MediaType == MediaType.Video)
            {
                result = await PublishVideoToInstagramAsync(post, accessToken, cancellationToken);

                // Video flow handles its own state transitions for processing retries.
                // If ScheduleProcessingRetryAsync was called, it returns Success=true with no ExternalPostId
                // (the post is in Processing, not Published). Only proceed to MarkPublished if we got an ID.
                if (result.Success && string.IsNullOrEmpty(result.ExternalPostId))
                {
                    // Processing retry scheduled — post is already in Processing state
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
        catch (Exception ex) when (IsTransientException(ex))
        {
            _logger.LogError(ex, "Transient error publishing Instagram post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Transient error: {ex.Message}"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Internal error (non-retryable) publishing Instagram post {PostId}: {ExceptionType}", postId, ex.GetType().Name);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Internal error (non-retryable): {ex.GetType().Name}: {ex.Message}"),
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
        var mediaUrl = await ResolveMediaUrlAsync(post, cancellationToken);

        // Pre-validate user_tags before sending to Meta
        var userTagsJson = post.InstagramUserTags;
        if (!string.IsNullOrEmpty(userTagsJson))
        {
            _logger.LogInformation(
                "[USER_TAGS] Post {PostId}: user_tags present on entity",
                post.Id);
            _logger.LogDebug(
                "[USER_TAGS] Post {PostId}: raw value: {UserTags}",
                post.Id, userTagsJson);

            // Warn if any tag is missing x/y coordinates (IMAGE-only field)
            try
            {
                var tags = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(userTagsJson);
                if (tags != null)
                {
                    var missingCoords = tags
                        .Where(t => !t.ContainsKey("x") || !t.ContainsKey("y"))
                        .Select(t => t.TryGetValue("username", out var u) ? u.GetString() : "(unknown)")
                        .ToList();

                    if (missingCoords.Count > 0)
                    {
                        _logger.LogWarning(
                            "[IG_DEBUG] user_tags WARNING: {Count} tag(s) missing x/y coordinates — usernames: {Usernames}. " +
                            "Meta may silently ignore tags without coordinates.",
                            missingCoords.Count, string.Join(", ", missingCoords));
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "[IG_DEBUG] user_tags WARNING: failed to parse user_tags for validation — raw: {UserTags}",
                    userTagsJson);
            }
        }
        else
        {
            _logger.LogInformation(
                "[USER_TAGS] Post {PostId}: no user_tags on entity (null/empty)",
                post.Id);
        }

        // Create image container (include user_tags if present)
        var containerResult = await CreateImageContainerAsync(
            igUserId, mediaUrl, post.Content, accessToken, cancellationToken,
            userTagsJson: userTagsJson);

        if (!containerResult.Success)
            return containerResult;

        var creationId = containerResult.ExternalPostId!;
        _logger.LogInformation(
            "[IG_PUBLISH] Step 1 DONE — IG container created. creation_id={CreationId} for post {PostId}",
            creationId, post.Id);

        // Poll for container to be ready (images are fast)
        var pollResult = await PollContainerStatusInProcessAsync(
            creationId, accessToken, _maxImagePollAttempts, _imagePollInterval, cancellationToken);

        if (!pollResult.Success)
            return pollResult;

        // Publish the container
        var publishResult = await PublishMediaContainerAsync(igUserId, creationId, accessToken, cancellationToken);

        if (publishResult.Success)
        {
            _logger.LogInformation(
                "[IG_PUBLISH] Step 2 DONE — IG media published. ig_media_id={IgMediaId} for post {PostId} (container creation_id={CreationId})",
                publishResult.ExternalPostId, post.Id, creationId);
        }

        return publishResult;
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

        // Pre-validate user_tags before sending to Meta (same validation as image flow)
        var userTagsJson = post.InstagramUserTags;
        if (!string.IsNullOrEmpty(userTagsJson))
        {
            _logger.LogInformation(
                "[USER_TAGS] Post {PostId}: user_tags present on VIDEO entity",
                post.Id);
            _logger.LogDebug(
                "[USER_TAGS] Post {PostId}: raw value: {UserTags}",
                post.Id, userTagsJson);

            try
            {
                var tags = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(userTagsJson);
                if (tags != null)
                {
                    var missingCoords = tags
                        .Where(t => !t.ContainsKey("x") || !t.ContainsKey("y"))
                        .Select(t => t.TryGetValue("username", out var u) ? u.GetString() : "(unknown)")
                        .ToList();

                    if (missingCoords.Count > 0)
                    {
                        _logger.LogWarning(
                            "[IG_DEBUG] user_tags WARNING: {Count} tag(s) missing x/y coordinates — usernames: {Usernames}. " +
                            "Meta may silently ignore tags without coordinates on video posts.",
                            missingCoords.Count, string.Join(", ", missingCoords));
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "[IG_DEBUG] user_tags WARNING: failed to parse user_tags for validation — raw: {UserTags}",
                    userTagsJson);
            }
        }
        else
        {
            _logger.LogInformation(
                "[USER_TAGS] Post {PostId}: no user_tags on VIDEO entity (null/empty)",
                post.Id);
        }

        // Step A: Create container if we don't have one yet
        if (string.IsNullOrEmpty(post.InstagramCreationId))
        {
            var mediaUrl = await ResolveMediaUrlAsync(post, cancellationToken);

            var containerResult = await CreateVideoContainerAsync(
                igUserId, mediaUrl, post.Content, accessToken, cancellationToken,
                userTagsJson: userTagsJson);

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
        // Guard: never retry a canceled post
        var freshStatus = await _dbContext.Posts
            .Where(p => p.Id == post.Id)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (freshStatus == PostStatus.Canceled)
        {
            _logger.LogInformation("Post {PostId} was canceled, skipping processing retry", post.Id);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Post was canceled");
        }

        post.ProcessingPollCount++;

        if (post.ProcessingPollCount >= Post.MaxProcessingPollCount)
        {
            _logger.LogWarning(
                "IG video post {PostId} exceeded max processing polls ({Max}), failing with timeout",
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

        // Return success=false but NOT a hard failure — the caller won't call HandlePublishFailureAsync
        // because we already handled the state transition here.
        return new PublishResult(true);
    }

    // ──────────────────────────────────────────────
    //  CAROUSEL FLOW (stateful, multi-attempt)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Stateful Instagram carousel publishing flow.
    /// Steps:
    ///   1. Create child containers for each image (is_carousel_item=true, no caption)
    ///   2. Create carousel container (media_type=CAROUSEL, children=..., caption=...)
    ///   3. Poll carousel container status
    ///   4. Publish carousel container
    ///
    /// Each step is idempotent — IDs are persisted so retries skip completed steps.
    /// Uses ProcessingPollCount (separate from RetryCount) for IN_PROGRESS polling.
    /// </summary>
    private async Task<PublishResult> PublishCarouselToInstagramAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;
        var mediaItems = post.MediaItems!.OrderBy(m => m.Order).ToList();

        _logger.LogInformation(
            "Starting carousel publish for post {PostId} with {Count} images",
            post.Id, mediaItems.Count);

        // Deserialize per-media-item tags (if any)
        var perItemTags = DeserializeMediaTags(post.InstagramMediaTagsJson);
        if (perItemTags.Count > 0)
            _logger.LogInformation("Carousel has per-item tags for {Count} media items", perItemTags.Count);

        // Step 1: Create child containers (idempotent — skip if already created)
        var childIds = DeserializeChildIds(post.InstagramChildCreationIds);

        if (childIds.Count < mediaItems.Count)
        {
            // Some or all children need to be created
            for (int i = childIds.Count; i < mediaItems.Count; i++)
            {
                var item = mediaItems[i];
                var imageUrl = await ResolveMediaUrlForItemAsync(item, cancellationToken);
                perItemTags.TryGetValue(item.Order, out var itemTagsJson);

                var childResult = await CreateCarouselChildContainerAsync(
                    igUserId, imageUrl, accessToken, cancellationToken, itemTagsJson);

                if (!childResult.Success)
                {
                    _logger.LogWarning(
                        "Failed to create carousel child {Index} for post {PostId}: {Error}",
                        i, post.Id, childResult.ErrorMessage);
                    return childResult;
                }

                childIds.Add(childResult.ExternalPostId!);

                // Persist after each child so we don't recreate on retry
                post.InstagramChildCreationIds = SerializeChildIds(childIds);
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Created carousel child {Index}/{Total}: {ChildId} for post {PostId}",
                    i + 1, mediaItems.Count, childResult.ExternalPostId, post.Id);
            }
        }

        // Step 2: Create carousel container (idempotent)
        if (string.IsNullOrEmpty(post.InstagramCarouselCreationId))
        {
            var carouselResult = await CreateCarouselContainerAsync(
                igUserId, childIds, post.Content, accessToken, cancellationToken);

            if (!carouselResult.Success)
                return carouselResult;

            post.InstagramCarouselCreationId = carouselResult.ExternalPostId!;
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Created carousel container {CarouselId} for post {PostId}",
                post.InstagramCarouselCreationId, post.Id);
        }

        // Step 3: Check carousel container status (single check, no blocking loop)
        var statusResult = await CheckContainerStatusAsync(
            post.InstagramCarouselCreationId!, accessToken, cancellationToken);

        switch (statusResult.Status)
        {
            case IgContainerStatus.Finished:
                _logger.LogInformation(
                    "Carousel container {CarouselId} is FINISHED, publishing for post {PostId}",
                    post.InstagramCarouselCreationId, post.Id);

                return await PublishMediaContainerAsync(
                    igUserId, post.InstagramCarouselCreationId!, accessToken, cancellationToken);

            case IgContainerStatus.InProgress:
                return await ScheduleProcessingRetryAsync(post, cancellationToken);

            case IgContainerStatus.Error:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Carousel container processing failed: {statusResult.ErrorMessage}");

            case IgContainerStatus.Expired:
                // Clear carousel container so it can be recreated on retry
                // (children are still valid if not expired)
                post.InstagramCarouselCreationId = null;
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Carousel container expired before publishing");

            default:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Unknown carousel container status: {statusResult.Status}");
        }
    }

    // ──────────────────────────────────────────────
    //  VIDEO CAROUSEL FLOW (stateful, multi-attempt)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Stateful Instagram video carousel publishing flow.
    /// Same structure as image carousel, but children use video_url instead of image_url.
    /// Video children may take time to process, so each child is polled via the stateful retry mechanism.
    ///
    /// Steps:
    ///   1. Create child containers for each video (video_url, is_carousel_item=true, no caption)
    ///   2. Check if all children are FINISHED (poll one-by-one)
    ///   3. Create carousel container (media_type=CAROUSEL, children=..., caption=...)
    ///   4. Poll carousel container status
    ///   5. Publish carousel container
    /// </summary>
    private async Task<PublishResult> PublishVideoCarouselToInstagramAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;
        var mediaItems = post.MediaItems!.OrderBy(m => m.Order).ToList();

        _logger.LogInformation(
            "Starting video carousel publish for post {PostId} with {Count} videos",
            post.Id, mediaItems.Count);

        // Deserialize per-media-item tags (if any)
        var perItemTags = DeserializeMediaTags(post.InstagramMediaTagsJson);
        if (perItemTags.Count > 0)
            _logger.LogInformation("Video carousel has per-item tags for {Count} media items", perItemTags.Count);

        // Step 1: Create child containers (idempotent — skip if already created)
        var childIds = DeserializeChildIds(post.InstagramChildCreationIds);

        if (childIds.Count < mediaItems.Count)
        {
            for (int i = childIds.Count; i < mediaItems.Count; i++)
            {
                var item = mediaItems[i];
                var videoUrl = await ResolveMediaUrlForItemAsync(item, cancellationToken, _videoDownloadUrlExpiration);
                perItemTags.TryGetValue(item.Order, out var itemTagsJson);

                var childResult = await CreateCarouselVideoChildContainerAsync(
                    igUserId, videoUrl, accessToken, cancellationToken, itemTagsJson);

                if (!childResult.Success)
                {
                    _logger.LogWarning(
                        "Failed to create video carousel child {Index} for post {PostId}: {Error}",
                        i, post.Id, childResult.ErrorMessage);
                    return childResult;
                }

                childIds.Add(childResult.ExternalPostId!);

                // Persist after each child so we don't recreate on retry
                post.InstagramChildCreationIds = SerializeChildIds(childIds);
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Created video carousel child {Index}/{Total}: {ChildId} for post {PostId}",
                    i + 1, mediaItems.Count, childResult.ExternalPostId, post.Id);
            }
        }

        // Step 2: Before creating parent container, check all children are FINISHED
        // (Video children may still be processing)
        if (string.IsNullOrEmpty(post.InstagramCarouselCreationId))
        {
            for (int i = 0; i < childIds.Count; i++)
            {
                var childStatus = await CheckContainerStatusAsync(
                    childIds[i], accessToken, cancellationToken);

                switch (childStatus.Status)
                {
                    case IgContainerStatus.Finished:
                        continue; // This child is ready

                    case IgContainerStatus.InProgress:
                        _logger.LogInformation(
                            "Video carousel child {Index} ({ChildId}) still processing for post {PostId}",
                            i, childIds[i], post.Id);
                        return await ScheduleProcessingRetryAsync(post, cancellationToken);

                    case IgContainerStatus.Error:
                        return new PublishResult(false,
                            ErrorType: PublishErrorType.Permanent,
                            ErrorMessage: $"Video carousel child {i} processing failed: {childStatus.ErrorMessage}");

                    case IgContainerStatus.Expired:
                        // Child expired; clear all children to start over
                        post.InstagramChildCreationIds = null;
                        post.UpdatedAt = DateTime.UtcNow;
                        await _dbContext.SaveChangesAsync(cancellationToken);
                        return new PublishResult(false,
                            ErrorType: PublishErrorType.Transient,
                            ErrorMessage: $"Video carousel child {i} expired before publishing");

                    default:
                        return new PublishResult(false,
                            ErrorType: PublishErrorType.Transient,
                            ErrorMessage: $"Unknown video carousel child status: {childStatus.Status}");
                }
            }
        }

        // Step 3: Create carousel container (idempotent)
        if (string.IsNullOrEmpty(post.InstagramCarouselCreationId))
        {
            var carouselResult = await CreateCarouselContainerAsync(
                igUserId, childIds, post.Content, accessToken, cancellationToken);

            if (!carouselResult.Success)
                return carouselResult;

            post.InstagramCarouselCreationId = carouselResult.ExternalPostId!;
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Created video carousel container {CarouselId} for post {PostId}",
                post.InstagramCarouselCreationId, post.Id);
        }

        // Step 4: Check carousel container status (single check, no blocking loop)
        var statusResult = await CheckContainerStatusAsync(
            post.InstagramCarouselCreationId!, accessToken, cancellationToken);

        switch (statusResult.Status)
        {
            case IgContainerStatus.Finished:
                _logger.LogInformation(
                    "Video carousel container {CarouselId} is FINISHED, publishing for post {PostId}",
                    post.InstagramCarouselCreationId, post.Id);

                return await PublishMediaContainerAsync(
                    igUserId, post.InstagramCarouselCreationId!, accessToken, cancellationToken);

            case IgContainerStatus.InProgress:
                return await ScheduleProcessingRetryAsync(post, cancellationToken);

            case IgContainerStatus.Error:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Video carousel container processing failed: {statusResult.ErrorMessage}");

            case IgContainerStatus.Expired:
                post.InstagramCarouselCreationId = null;
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Video carousel container expired before publishing");

            default:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Unknown video carousel container status: {statusResult.Status}");
        }
    }

    // ──────────────────────────────────────────────
    //  MIXED CAROUSEL FLOW (stateful, multi-attempt)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Stateful Instagram mixed carousel publishing flow (images + videos in any order).
    /// Same structure as video carousel, but each child uses image_url or video_url depending on its type.
    /// Video children may take time to process, so all children are polled before creating the parent.
    ///
    /// Steps:
    ///   1. Create child containers for each asset (image or video) with is_carousel_item=true, no caption
    ///   2. Check if all children are FINISHED (video children may still be processing)
    ///   3. Create carousel container (media_type=CAROUSEL, children=..., caption=...)
    ///   4. Poll carousel container status
    ///   5. Publish carousel container
    /// </summary>
    private async Task<PublishResult> PublishMixedCarouselToInstagramAsync(
        Post post, string accessToken, CancellationToken cancellationToken)
    {
        var igUserId = post.TargetInstagramAccount!.IgBusinessId;
        var mediaItems = post.MediaItems!.OrderBy(m => m.Order).ToList();

        _logger.LogInformation(
            "Starting mixed carousel publish for post {PostId} with {Count} items ({Images} images, {Videos} videos)",
            post.Id, mediaItems.Count,
            mediaItems.Count(m => m.MediaType == Enums.MediaType.Image),
            mediaItems.Count(m => m.MediaType == Enums.MediaType.Video));

        // Deserialize per-media-item tags (if any)
        var perItemTags = DeserializeMediaTags(post.InstagramMediaTagsJson);
        if (perItemTags.Count > 0)
            _logger.LogInformation("Mixed carousel has per-item tags for {Count} media items", perItemTags.Count);

        // Step 1: Create child containers (idempotent — skip if already created)
        var childIds = DeserializeChildIds(post.InstagramChildCreationIds);

        if (childIds.Count < mediaItems.Count)
        {
            for (int i = childIds.Count; i < mediaItems.Count; i++)
            {
                var item = mediaItems[i];
                perItemTags.TryGetValue(item.Order, out var itemTagsJson);
                PublishResult childResult;

                if (item.MediaType == Enums.MediaType.Video)
                {
                    var videoUrl = await ResolveMediaUrlForItemAsync(item, cancellationToken, _videoDownloadUrlExpiration);
                    childResult = await CreateCarouselVideoChildContainerAsync(
                        igUserId, videoUrl, accessToken, cancellationToken, itemTagsJson);
                }
                else
                {
                    var imageUrl = await ResolveMediaUrlForItemAsync(item, cancellationToken);
                    childResult = await CreateCarouselChildContainerAsync(
                        igUserId, imageUrl, accessToken, cancellationToken, itemTagsJson);
                }

                if (!childResult.Success)
                {
                    _logger.LogWarning(
                        "Failed to create mixed carousel child {Index} ({Type}) for post {PostId}: {Error}",
                        i, item.MediaType, post.Id, childResult.ErrorMessage);
                    return childResult;
                }

                childIds.Add(childResult.ExternalPostId!);

                // Persist after each child so we don't recreate on retry
                post.InstagramChildCreationIds = SerializeChildIds(childIds);
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Created mixed carousel child {Index}/{Total} ({Type}): {ChildId} for post {PostId}",
                    i + 1, mediaItems.Count, item.MediaType, childResult.ExternalPostId, post.Id);
            }
        }

        // Step 2: Before creating parent container, check all children are FINISHED
        // (Video children may still be processing)
        if (string.IsNullOrEmpty(post.InstagramCarouselCreationId))
        {
            for (int i = 0; i < childIds.Count; i++)
            {
                var childStatus = await CheckContainerStatusAsync(
                    childIds[i], accessToken, cancellationToken);

                switch (childStatus.Status)
                {
                    case IgContainerStatus.Finished:
                        continue; // This child is ready

                    case IgContainerStatus.InProgress:
                        _logger.LogInformation(
                            "Mixed carousel child {Index} ({ChildId}) still processing for post {PostId}",
                            i, childIds[i], post.Id);
                        return await ScheduleProcessingRetryAsync(post, cancellationToken);

                    case IgContainerStatus.Error:
                        return new PublishResult(false,
                            ErrorType: PublishErrorType.Permanent,
                            ErrorMessage: $"Mixed carousel child {i} processing failed: {childStatus.ErrorMessage}");

                    case IgContainerStatus.Expired:
                        // Child expired; clear all children to start over
                        post.InstagramChildCreationIds = null;
                        post.UpdatedAt = DateTime.UtcNow;
                        await _dbContext.SaveChangesAsync(cancellationToken);
                        return new PublishResult(false,
                            ErrorType: PublishErrorType.Transient,
                            ErrorMessage: $"Mixed carousel child {i} expired before publishing");

                    default:
                        return new PublishResult(false,
                            ErrorType: PublishErrorType.Transient,
                            ErrorMessage: $"Unknown mixed carousel child status: {childStatus.Status}");
                }
            }
        }

        // Step 3: Create carousel container (idempotent)
        if (string.IsNullOrEmpty(post.InstagramCarouselCreationId))
        {
            var carouselResult = await CreateCarouselContainerAsync(
                igUserId, childIds, post.Content, accessToken, cancellationToken);

            if (!carouselResult.Success)
                return carouselResult;

            post.InstagramCarouselCreationId = carouselResult.ExternalPostId!;
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Created mixed carousel container {CarouselId} for post {PostId}",
                post.InstagramCarouselCreationId, post.Id);
        }

        // Step 4: Check carousel container status (single check, no blocking loop)
        var statusResult = await CheckContainerStatusAsync(
            post.InstagramCarouselCreationId!, accessToken, cancellationToken);

        switch (statusResult.Status)
        {
            case IgContainerStatus.Finished:
                _logger.LogInformation(
                    "Mixed carousel container {CarouselId} is FINISHED, publishing for post {PostId}",
                    post.InstagramCarouselCreationId, post.Id);

                return await PublishMediaContainerAsync(
                    igUserId, post.InstagramCarouselCreationId!, accessToken, cancellationToken);

            case IgContainerStatus.InProgress:
                return await ScheduleProcessingRetryAsync(post, cancellationToken);

            case IgContainerStatus.Error:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Mixed carousel container processing failed: {statusResult.ErrorMessage}");

            case IgContainerStatus.Expired:
                post.InstagramCarouselCreationId = null;
                post.UpdatedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);

                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Mixed carousel container expired before publishing");

            default:
                return new PublishResult(false,
                    ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Unknown mixed carousel container status: {statusResult.Status}");
        }
    }

    /// <summary>
    /// POST /{ig-user-id}/media with media_type=VIDEO, video_url, and is_carousel_item=true (no caption on children).
    /// Used for video carousel items. media_type=VIDEO is required — without it the API defaults to
    /// an image container and returns error 100 "The parameter image_url is required".
    /// </summary>
    private async Task<PublishResult> CreateCarouselVideoChildContainerAsync(
        string igUserId, string videoUrl,
        string accessToken, CancellationToken cancellationToken,
        string? userTagsJson = null)
    {
        var url = $"{_graphApiBaseUrl}/{igUserId}/media";
        var formFields = new Dictionary<string, string>
        {
            ["media_type"] = "VIDEO",
            ["video_url"] = videoUrl,
            ["is_carousel_item"] = "true",
            ["access_token"] = accessToken,
        };

        // Video tags must NOT include x/y positions
        var videoTagsJson = StripPositionsFromUserTags(userTagsJson);
        if (!string.IsNullOrEmpty(videoTagsJson))
        {
            formFields["user_tags"] = videoTagsJson;
            _logger.LogInformation("IG_VIDEO_CHILD user_tags included (username-only): {Tags}", videoTagsJson);
        }

        var content = new FormUrlEncodedContent(formFields);

        _logger.LogInformation(PostPilotLogEvents.OutboundCall,
            "IG_VIDEO_CHILD_OUTBOUND POST {Url}", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "IG_VIDEO_CHILD_RESPONSE {StatusCode}", response.StatusCode);
        _logger.LogDebug("IG_VIDEO_CHILD_RESPONSE_BODY body={Body}", SanitizeForLog(responseBody));

        return ParseMetaIdResponse(response, responseBody, "video carousel child container creation");
    }

    /// <summary>
    /// POST /{ig-user-id}/media with image_url and is_carousel_item=true (no caption on children).
    /// </summary>
    private async Task<PublishResult> CreateCarouselChildContainerAsync(
        string igUserId, string imageUrl,
        string accessToken, CancellationToken cancellationToken,
        string? userTagsJson = null)
    {
        var url = $"{_graphApiBaseUrl}/{igUserId}/media";
        var formFields = new Dictionary<string, string>
        {
            ["image_url"] = imageUrl,
            ["is_carousel_item"] = "true",
            ["access_token"] = accessToken,
        };

        // Image tags include x/y positions
        if (!string.IsNullOrEmpty(userTagsJson))
        {
            formFields["user_tags"] = userTagsJson;
            _logger.LogInformation("IG_IMG_CHILD user_tags included: {Tags}", userTagsJson);
        }

        var content = new FormUrlEncodedContent(formFields);

        _logger.LogInformation(PostPilotLogEvents.OutboundCall,
            "IG_IMG_CHILD_OUTBOUND POST {Url}", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "IG_IMG_CHILD_RESPONSE {StatusCode}", response.StatusCode);
        _logger.LogDebug("IG_IMG_CHILD_RESPONSE_BODY body={Body}", SanitizeForLog(responseBody));

        return ParseMetaIdResponse(response, responseBody, "carousel child container creation");
    }

    /// <summary>
    /// POST /{ig-user-id}/media with media_type=CAROUSEL, children=..., and caption.
    /// </summary>
    private async Task<PublishResult> CreateCarouselContainerAsync(
        string igUserId, List<string> childCreationIds, string caption,
        string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{igUserId}/media";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["media_type"] = "CAROUSEL",
            ["children"] = string.Join(",", childCreationIds),
            ["caption"] = caption ?? "",
            ["access_token"] = accessToken,
        });

        _logger.LogInformation(PostPilotLogEvents.OutboundCall,
            "IG_CAROUSEL_OUTBOUND POST {Url} childCount={Count}", url, childCreationIds.Count);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "IG_CAROUSEL_RESPONSE {StatusCode}", response.StatusCode);
        _logger.LogDebug("IG_CAROUSEL_RESPONSE_BODY body={Body}", SanitizeForLog(responseBody));

        return ParseMetaIdResponse(response, responseBody, "carousel container creation");
    }

    /// <summary>
    /// Resolves a public URL for a PostMediaItem (generates a fresh signed download URL if storage key).
    /// </summary>
    private async Task<string> ResolveMediaUrlForItemAsync(PostMediaItem item, CancellationToken cancellationToken, TimeSpan? expiration = null)
    {
        if (_mediaService.IsStorageKey(item.MediaUrl))
        {
            return await _mediaService.GetPublishingUrlAsync(item.MediaUrl, expiration ?? _mediaDownloadUrlExpiration, cancellationToken);
        }
        return item.MediaUrl;
    }

    private static List<string> DeserializeChildIds(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string SerializeChildIds(List<string> ids)
    {
        return JsonSerializer.Serialize(ids);
    }

    /// <summary>
    /// Deserializes InstagramMediaTagsJson into a dictionary keyed by media item Order (0-based).
    /// Returns empty dictionary if null/empty/invalid JSON.
    /// </summary>
    internal static Dictionary<int, string> DeserializeMediaTags(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new Dictionary<int, string>();

        try
        {
            // JSON format: {"0":[{"username":"nike","x":0.5,"y":0.5}],"2":[...]}
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (raw == null) return new Dictionary<int, string>();

            var result = new Dictionary<int, string>();
            foreach (var (key, value) in raw)
            {
                if (int.TryParse(key, out var order) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0)
                {
                    result[order] = value.GetRawText();
                }
            }
            return result;
        }
        catch (JsonException)
        {
            return new Dictionary<int, string>();
        }
    }

    // ──────────────────────────────────────────────
    //  GRAPH API METHODS
    // ──────────────────────────────────────────────

    /// <summary>
    /// Resolves a public URL for the post's media (generates a fresh signed download URL if storage key).
    /// </summary>
    private async Task<string> ResolveMediaUrlAsync(Post post, CancellationToken cancellationToken)
    {
        if (_mediaService.IsStorageKey(post.MediaUrl!))
        {
            var url = await _mediaService.GetPublishingUrlAsync(post.MediaUrl!, _mediaDownloadUrlExpiration, cancellationToken);
            _logger.LogInformation("Generated publishing URL for storage key {StorageKey} for IG post {PostId}",
                post.MediaUrl, post.Id);
            return url;
        }
        return post.MediaUrl!;
    }

    /// <summary>
    /// Resolves the page access token for an Instagram Business Account
    /// by looking up the linked Facebook Page.
    ///
    /// Defensive workspace scoping: in theory two workspaces could each own a
    /// ConnectedPage with the same external PageId (the agency case), so we
    /// must filter by the IG account's WorkspaceId. The legitimate callers
    /// already pass an IG account fetched in-workspace, but the cheap
    /// `WorkspaceId == ...` predicate makes the guarantee local and obvious.
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

    /// <summary>
    /// POST /{ig-user-id}/media with image_url and caption (image container).
    /// Optionally includes user_tags for tagging people on the image.
    ///
    /// user_tags format: JSON array string, e.g. [{"username":"nike","x":0.5,"y":0.5}]
    /// Sent as form-urlencoded field — Meta expects the JSON string as the value.
    /// </summary>
    private async Task<PublishResult> CreateImageContainerAsync(
        string igUserId, string imageUrl, string caption,
        string accessToken, CancellationToken cancellationToken,
        string? userTagsJson = null)
    {
        var url = $"{_graphApiBaseUrl}/{igUserId}/media";
        var formFields = new Dictionary<string, string>
        {
            ["image_url"] = imageUrl,
            ["caption"] = caption ?? "",
            ["access_token"] = accessToken,
        };

        if (!string.IsNullOrEmpty(userTagsJson))
        {
            formFields["user_tags"] = userTagsJson;
        }

        var content = new FormUrlEncodedContent(formFields);

        // ── IG CREATE CONTAINER start (image) ──
        _logger.LogInformation(PostPilotLogEvents.OutboundCall,
            "IG_OUTBOUND POST {Url} igUserId={IgUserId} mediaType=IMAGE",
            url, igUserId);
        _logger.LogDebug(
            "IG_OUTBOUND_PARAMS image_url={ImageUrl} caption={Caption} user_tags={UserTags}",
            RedactUrl(imageUrl), TruncateCaption(caption), userTagsJson ?? "(none)");

        // Log the full form body (sanitized) at debug only
        var debugBody = await content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("IG_OUTBOUND_BODY {Body}", SanitizeForLog(debugBody));

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // ── IG CREATE CONTAINER response ──
        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "IG_RESPONSE {StatusCode} step=image-container",
            (int)response.StatusCode);
        _logger.LogDebug("IG_RESPONSE_BODY body={Body}", SanitizeForLog(responseBody));

        var parsed = ParseMetaIdResponse(response, responseBody, "image container creation");

        _logger.LogDebug(
            "IG_CONTAINER_PARSED success={Success} creation_id={CreationId}",
            parsed.Success, parsed.ExternalPostId ?? "(null)");

        return parsed;
    }

    /// <summary>
    /// POST /{ig-user-id}/media with media_type=REELS, video_url, and caption (video container).
    /// Optionally includes user_tags for tagging people on the video/reel.
    ///
    /// Important: Instagram requires that video/reel user_tags contain ONLY username (no x/y positions).
    /// Image tags use [{"username":"nike","x":0.5,"y":0.5}] but video tags must be [{"username":"nike"}].
    /// This method strips x/y from the stored tag JSON before sending.
    ///
    /// Fallback: if the request still fails with code=100 (Invalid parameter) AND user_tags were sent,
    /// retries once without user_tags entirely. The post is still published — just without tags.
    /// </summary>
    private async Task<PublishResult> CreateVideoContainerAsync(
        string igUserId, string videoUrl, string caption,
        string accessToken, CancellationToken cancellationToken,
        string? userTagsJson = null)
    {
        var url = $"{_graphApiBaseUrl}/{igUserId}/media";

        // Build base form fields (always present)
        var baseFields = new Dictionary<string, string>
        {
            ["media_type"] = "REELS",
            ["video_url"] = videoUrl,
            ["caption"] = caption ?? "",
            ["access_token"] = accessToken,
        };

        // Strip x/y positions from tags — IG rejects positions on video/reel containers.
        // DB stores [{"username":"nike","x":0.5,"y":0.5}], but video must send [{"username":"nike"}].
        var videoTagsJson = StripPositionsFromUserTags(userTagsJson);
        var hasTags = !string.IsNullOrEmpty(videoTagsJson);

        // First attempt: include user_tags (username-only) if present
        var formFields = new Dictionary<string, string>(baseFields);
        if (hasTags)
        {
            formFields["user_tags"] = videoTagsJson!;
        }

        // ── IG_CREATE_CONTAINER request (video, attempt 1) ──
        LogVideoContainerRequest(url, igUserId, formFields, videoUrl, caption, videoTagsJson, attempt: 1);

        var content = new FormUrlEncodedContent(formFields);
        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // ── IG_CREATE_CONTAINER response ──
        LogVideoContainerResponse(response, responseBody, attempt: 1);

        // Fallback: if tags still caused "Invalid parameter" (code=100), retry without them entirely
        if (hasTags && !response.IsSuccessStatusCode)
        {
            var shouldRetryWithoutTags = false;
            try
            {
                var errBody = JsonSerializer.Deserialize<MetaErrorResponseIg>(responseBody);
                var code = errBody?.Error?.Code ?? 0;
                var subcode = errBody?.Error?.ErrorSubcode;
                var message = errBody?.Error?.Message ?? "";

                if (code == 100 &&
                    (subcode == 2207064 ||
                     message.Contains("Invalid parameter", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("user tag", StringComparison.OrdinalIgnoreCase)))
                {
                    shouldRetryWithoutTags = true;
                }
            }
            catch (JsonException)
            {
                // Can't parse error body — don't retry, fall through to normal failure
            }

            if (shouldRetryWithoutTags)
            {
                _logger.LogWarning(PostPilotLogEvents.OutboundError,
                    "IG_CREATE_CONTAINER FALLBACK: IG rejected user_tags for video container (code=100). " +
                    "Retrying WITHOUT tags. igUserId={IgUserId} videoTags={VideoTags}",
                    igUserId, videoTagsJson);

                var retryFields = new Dictionary<string, string>(baseFields);

                LogVideoContainerRequest(url, igUserId, retryFields, videoUrl, caption, userTagsJson: null, attempt: 2);

                var retryContent = new FormUrlEncodedContent(retryFields);
                response = await _httpClient.PostAsync(url, retryContent, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                LogVideoContainerResponse(response, responseBody, attempt: 2);
            }
        }

        var parsed = ParseMetaIdResponse(response, responseBody, "video container creation");

        _logger.LogDebug(
            "IG_CREATE_CONTAINER PARSED success={Success} creation_id={CreationId}",
            parsed.Success, parsed.ExternalPostId ?? "(null)");

        return parsed;
    }

    /// <summary>
    /// Strips x/y position fields from user_tags JSON, returning username-only tags.
    /// Instagram requires that video/reel tags do NOT include position coordinates.
    ///
    /// Input:  [{"username":"nike","x":0.5,"y":0.5},{"username":"adidas","x":0.1,"y":0.9}]
    /// Output: [{"username":"nike"},{"username":"adidas"}]
    ///
    /// Returns null if input is null/empty or parsing fails.
    /// </summary>
    internal static string? StripPositionsFromUserTags(string? userTagsJson)
    {
        if (string.IsNullOrEmpty(userTagsJson))
            return null;

        try
        {
            var tags = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(userTagsJson);
            if (tags == null || tags.Count == 0)
                return null;

            // Extract only username from each tag object
            var usernameOnly = tags
                .Select(t => new { username = t.TryGetValue("username", out var u) ? u.GetString() : null })
                .Where(t => !string.IsNullOrEmpty(t.username))
                .ToList();

            if (usernameOnly.Count == 0)
                return null;

            return JsonSerializer.Serialize(usernameOnly);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Structured request log for video container creation.
    /// Logs all outgoing parameters as key-value pairs with sensitive fields redacted.
    /// </summary>
    private void LogVideoContainerRequest(
        string url, string igUserId, Dictionary<string, string> formFields,
        string videoUrl, string caption, string? userTagsJson, int attempt)
    {
        // Build a sanitized copy of all params for structured logging
        var sanitizedParams = new Dictionary<string, string>();
        foreach (var kvp in formFields)
        {
            sanitizedParams[kvp.Key] = kvp.Key switch
            {
                "access_token" => "***REDACTED***",
                "video_url" => RedactUrl(kvp.Value),
                "image_url" => RedactUrl(kvp.Value),
                _ => kvp.Value,
            };
        }

        var paramsJson = JsonSerializer.Serialize(sanitizedParams);

        _logger.LogInformation(PostPilotLogEvents.OutboundCall,
            "IG_CREATE_CONTAINER REQUEST attempt={Attempt} POST {Url} igUserId={IgUserId} " +
            "mediaType=REELS hasUserTags={HasTags} params={Params}",
            attempt, url, igUserId, !string.IsNullOrEmpty(userTagsJson), paramsJson);
    }

    /// <summary>
    /// Structured response log for video container creation.
    /// </summary>
    private void LogVideoContainerResponse(
        HttpResponseMessage response, string responseBody, int attempt)
    {
        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "IG_CREATE_CONTAINER RESPONSE attempt={Attempt} httpStatus={StatusCode} body={Body}",
            attempt, (int)response.StatusCode, SanitizeForLog(responseBody));
    }

    /// <summary>
    /// Single container status check (no looping). Returns parsed status.
    /// Used by the video flow for non-blocking status checks.
    /// </summary>
    private async Task<ContainerStatusResult> CheckContainerStatusAsync(
        string creationId, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{creationId}?fields=status_code,status&access_token={accessToken}";

        var response = await _httpClient.GetAsync(url, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("IG container status check failed: {StatusCode} - {Body}",
                response.StatusCode, SanitizeForLog(responseBody));

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
        var url = $"{_graphApiBaseUrl}/{igUserId}/media_publish";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["creation_id"] = creationId,
            ["access_token"] = accessToken,
        });

        // ── IG PUBLISH start ──
        _logger.LogInformation(PostPilotLogEvents.OutboundCall,
            "IG_MEDIA_PUBLISH_OUTBOUND POST {Url} creation_id={CreationId}",
            url, creationId);

        var debugBody = await content.ReadAsStringAsync(cancellationToken);
        _logger.LogDebug("IG_MEDIA_PUBLISH_BODY {Body}", SanitizeForLog(debugBody));

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // ── IG PUBLISH response ──
        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "IG_MEDIA_PUBLISH_RESPONSE {StatusCode}",
            (int)response.StatusCode);
        _logger.LogDebug("IG_MEDIA_PUBLISH_RESPONSE_BODY body={Body}", SanitizeForLog(responseBody));

        var parsed = ParseMetaIdResponse(response, responseBody, "media publish");

        _logger.LogDebug(
            "IG_MEDIA_PUBLISH_PARSED success={Success} ig_media_id={IgMediaId}",
            parsed.Success, parsed.ExternalPostId ?? "(null)");

        return parsed;
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
            var url = $"{_graphApiBaseUrl}/{mediaId}?fields=permalink,media_type&access_token={accessToken}";
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
        if (AuthErrorCodes.Contains(errorCode))
            return PublishErrorType.Auth;

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

        _logger.LogInformation(PostPilotLogEvents.PublishSuccess,
            "IG_PUBLISH_SUCCESS postId={PostId} externalPostId={ExternalPostId}",
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
        // Guard: never retry a canceled post
        var freshStatus = await _dbContext.Posts
            .Where(p => p.Id == post.Id)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (freshStatus == PostStatus.Canceled)
        {
            _logger.LogInformation("Post {PostId} was canceled, skipping retry", post.Id);
            return result;
        }

        post.RetryCount++;
        post.ErrorMessage = result.ErrorMessage;
        post.UpdatedAt = DateTime.UtcNow;

        if (result.ErrorType == PublishErrorType.Auth)
        {
            // Token invalid / session invalidated: fail the post (stays visible/retryable
            // in the same workspace), then flag the connection ReauthRequired WITHOUT
            // disconnecting, releasing ownership, or canceling future posts.
            post.Status = PostStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _providerConnections.MarkReauthRequiredAsync(
                post.WorkspaceId, ProviderType.Meta, cancellationToken);

            _logger.LogWarning(PostPilotLogEvents.PublishFail,
                "IG_PUBLISH_FAIL_AUTH postId={PostId} workspaceId={WorkspaceId} — marked ReauthRequired, ownership retained. error={Error}",
                post.Id, post.WorkspaceId, result.ErrorMessage);

            return result;
        }

        if (result.ErrorType == PublishErrorType.Permanent || post.RetryCount >= post.MaxRetries)
        {
            post.Status = PostStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(PostPilotLogEvents.PublishFail,
                "IG_PUBLISH_FAIL postId={PostId} retryCount={RetryCount} error={Error}",
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

        _logger.LogInformation(PostPilotLogEvents.RetryScheduled,
            "IG_RETRY_SCHEDULED postId={PostId} attempt={RetryCount}/{MaxRetries} retryAt={RetryAt} delayMin={DelayMinutes}",
            post.Id, post.RetryCount, post.MaxRetries, retryAt, delayMinutes);

        return result;
    }

    // ──────────────────────────────────────────────
    //  SANITIZATION HELPERS (for debug logging)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Redacts sensitive fields from URL-encoded bodies and JSON properties so secrets
    /// and signed URLs are never written to logs.
    ///
    /// Redacted fields: access_token, video_url, image_url.
    /// For URLs (video_url, image_url): keeps scheme + host + last 12 chars of path for traceability.
    /// </summary>
    private static string SanitizeForLog(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        // URL-encoded form: access_token=XXXXX& or access_token=XXXXX (at end)
        var result = System.Text.RegularExpressions.Regex.Replace(
            raw, @"access_token=[^&\s]+", "access_token=***REDACTED***");

        // JSON property: "access_token":"XXXXX"  (with or without spaces around colon)
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"""access_token""\s*:\s*""[^""]*""", "\"access_token\":\"***REDACTED***\"");

        // URL-encoded form: video_url=XXXXX& or video_url=XXXXX (at end)
        result = System.Text.RegularExpressions.Regex.Replace(
            result, @"(video_url|image_url)=[^&\s]+", match => RedactUrlFormField(match.Value));

        return result;
    }

    /// <summary>
    /// Redacts a URL-encoded form field value (e.g., "video_url=https://...long-signed-url")
    /// keeping only the field name, scheme+host, and last 12 chars of the path for traceability.
    /// </summary>
    private static string RedactUrlFormField(string formField)
    {
        var eqIdx = formField.IndexOf('=');
        if (eqIdx < 0) return formField;

        var key = formField[..eqIdx];
        var value = Uri.UnescapeDataString(formField[(eqIdx + 1)..]);
        return $"{key}={RedactUrl(value)}";
    }

    /// <summary>
    /// Redacts a URL to scheme+host + last 12 chars of path for traceability.
    /// Example: "https://storage.provider.example.com/...abc123def456"
    /// </summary>
    private static string RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(empty)";

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var tail = url.Length > 12 ? url[^12..] : url;
            return $"{uri.Scheme}://{uri.Host}/...{tail}";
        }

        // Not a valid URL — show first 20 + last 12 chars
        if (url.Length > 40)
            return $"{url[..20]}...{url[^12..]}";
        return url;
    }

    /// <summary>
    /// Truncates a caption to maxLen characters for safe, readable log output.
    /// </summary>
    private static string TruncateCaption(string? caption, int maxLen = 200)
    {
        if (string.IsNullOrEmpty(caption)) return "(empty)";
        return caption.Length <= maxLen ? caption : caption[..maxLen] + "…(truncated)";
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
