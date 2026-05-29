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
/// Publisher implementation for Facebook Pages using Meta Graph API.
/// Supports text-only, single image, single video, and multi-photo (2-10 images) posts.
/// </summary>
public class FacebookPagePublisher : IPostPublisher
{
    private readonly AppDbContext _dbContext;
    private readonly IPostScheduler _scheduler;
    private readonly IMediaService _mediaService;
    private readonly FeatureSettings _featureSettings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<FacebookPagePublisher> _logger;
    private readonly string _graphApiBaseUrl;
    private readonly TimeSpan _metaDownloadUrlExpiration;
    private readonly TimeSpan _videoDownloadUrlExpiration;

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
        197,  // Empty post (missing message or media)
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
        IMediaService mediaService,
        FeatureSettings featureSettings,
        HttpClient httpClient,
        ILogger<FacebookPagePublisher> logger,
        MetaApiOptions metaApiOptions,
        PublishingOptions publishingOptions)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _mediaService = mediaService;
        _featureSettings = featureSettings;
        _httpClient = httpClient;
        _logger = logger;
        _graphApiBaseUrl = metaApiOptions.GraphApiBaseUrl;
        _metaDownloadUrlExpiration = TimeSpan.FromMinutes(publishingOptions.MediaDownloadUrlExpirationMinutes);
        _videoDownloadUrlExpiration = TimeSpan.FromMinutes(publishingOptions.VideoDownloadUrlExpirationMinutes);
    }

    public async Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(PostPilotLogEvents.PublishStart, "FB_PUBLISH_START postId={PostId}", postId);

        // Step 1: Load post with target page and media items
        var post = await _dbContext.Posts
            .Include(p => p.TargetPage)
            .Include(p => p.MediaItems)
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

        // Establish per-publish scope so all subsequent logs include PostId, Platform, AccountId
        using var publishScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["PostId"]    = postId,
            ["Platform"]  = "Facebook",
            ["AccountId"] = post.TargetPage?.PageId ?? string.Empty
        });

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

        // Step 6: Call Meta Graph API
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
        catch (Exception ex) when (IsTransientException(ex))
        {
            _logger.LogError(ex, "Transient error publishing post {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Transient error: {ex.Message}"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Internal error (non-retryable) publishing post {PostId}: {ExceptionType}", postId, ex.GetType().Name);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Internal error (non-retryable): {ex.GetType().Name}: {ex.Message}"),
                cancellationToken);
        }
    }

    private async Task<bool> TryClaimPostAsync(Post post, CancellationToken cancellationToken)
    {
        // Optimistic concurrency - only claim if status is still Scheduled, RetryPending, or Processing
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

    internal async Task<PublishResult> CallMetaApiAsync(Post post, CancellationToken cancellationToken)
    {
        var pageId = post.TargetPage!.PageId;
        var accessToken = post.TargetPage.AccessToken;

        // Route to multi-photo flow if 2+ media items
        if (post.MediaItems?.Count >= 2)
        {
            return await PublishMultiPhotoAsync(post, pageId, accessToken, cancellationToken);
        }

        string url;
        HttpContent content;

        if (post.MediaType == MediaType.Video && !string.IsNullOrEmpty(post.MediaUrl))
        {
            // Video post - use videos endpoint
            return await PublishVideoAsync(post, pageId, accessToken, cancellationToken);
        }
        else if (post.MediaType == MediaType.Image && !string.IsNullOrEmpty(post.MediaUrl))
        {
            // Image post - use photos endpoint
            string imageUrl;
            if (_mediaService.IsStorageKey(post.MediaUrl))
            {
                imageUrl = _mediaService.GetPublishingUrl(post.MediaUrl, _metaDownloadUrlExpiration);
                _logger.LogInformation("Generated publishing URL for storage key {StorageKey} for post {PostId}",
                    post.MediaUrl, post.Id);
            }
            else
            {
                imageUrl = post.MediaUrl;
            }

            _logger.LogInformation("FB_IMAGE_URL postId={PostId} storageKey={MediaUrl} resolvedUrl={ImageUrl}",
                post.Id, post.MediaUrl, imageUrl);

            url = $"{_graphApiBaseUrl}/{pageId}/photos";
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["message"] = post.Content,
                ["url"] = imageUrl,
                ["access_token"] = accessToken
            });
        }
        else if (!string.IsNullOrEmpty(post.MediaUrl))
        {
            // Legacy: MediaType not set but MediaUrl exists - infer from URL extension
            var extension = Path.GetExtension(post.MediaUrl).ToLowerInvariant();
            if (extension == ".mp4")
            {
                return await PublishVideoAsync(post, pageId, accessToken, cancellationToken);
            }

            // Assume image for backward compatibility
            string imageUrl;
            if (_mediaService.IsStorageKey(post.MediaUrl))
            {
                imageUrl = _mediaService.GetPublishingUrl(post.MediaUrl, _metaDownloadUrlExpiration);
                _logger.LogInformation("Generated publishing URL for storage key {StorageKey} for post {PostId}",
                    post.MediaUrl, post.Id);
            }
            else
            {
                imageUrl = post.MediaUrl;
            }

            _logger.LogInformation("FB_IMAGE_URL postId={PostId} storageKey={MediaUrl} resolvedUrl={ImageUrl} (legacy branch)",
                post.Id, post.MediaUrl, imageUrl);

            url = $"{_graphApiBaseUrl}/{pageId}/photos";
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["message"] = post.Content,
                ["url"] = imageUrl,
                ["access_token"] = accessToken
            });
        }
        else
        {
            // Text-only post
            url = $"{_graphApiBaseUrl}/{pageId}/feed";
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["message"] = post.Content,
                ["access_token"] = accessToken
            });
        }

        _logger.LogInformation(PostPilotLogEvents.OutboundCall, "FB_OUTBOUND POST {Url} postId={PostId}", url, post.Id);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        sw.Stop();
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "FB_RESPONSE {StatusCode} postId={PostId} durationMs={DurationMs}",
            (int)response.StatusCode, post.Id, sw.ElapsedMilliseconds);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogInformation("FB_RESPONSE_BODY postId={PostId} body={Body}", post.Id, responseBody);
        }
        else
        {
            _logger.LogDebug("FB_RESPONSE_BODY postId={PostId} body={Body}", post.Id, responseBody);
        }

        return ParseMetaResponse(post.Id, response, responseBody);
    }

    // ──────────────────────────────────────────────
    //  MULTI-PHOTO FLOW (attached_media)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Publishes a multi-photo post (2-10 images) to a Facebook Page using the attached_media flow:
    /// 1. Upload each photo as unpublished: POST /{pageId}/photos?published=false
    /// 2. Create feed post with attached_media referencing all uploaded photos
    /// </summary>
    private async Task<PublishResult> PublishMultiPhotoAsync(
        Post post, string pageId, string accessToken, CancellationToken cancellationToken)
    {
        var mediaItems = post.MediaItems!.OrderBy(m => m.Order).ToList();

        _logger.LogInformation(
            "Starting FB multi-photo publish for post {PostId} with {Count} images",
            post.Id, mediaItems.Count);

        if (mediaItems.Count < 2)
        {
            _logger.LogError(
                "Multi-photo publish called with {Count} images for post {PostId} — need at least 2. Aborting.",
                mediaItems.Count, post.Id);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: $"Multi-photo post requires at least 2 images, got {mediaItems.Count}");
        }

        // Step 1: Upload each photo as unpublished
        var photoIds = new List<string>();

        for (int i = 0; i < mediaItems.Count; i++)
        {
            var item = mediaItems[i];
            var imageUrl = ResolveMediaUrlForItem(item);

            var uploadResult = await UploadUnpublishedPhotoAsync(
                pageId, imageUrl, accessToken, cancellationToken);

            if (!uploadResult.Success)
            {
                _logger.LogWarning(
                    "Failed to upload unpublished photo {Index}/{Total} for post {PostId}: {Error}. " +
                    "Aborting multi-photo publish — will NOT create feed post.",
                    i + 1, mediaItems.Count, post.Id, uploadResult.ErrorMessage);
                return uploadResult;
            }

            photoIds.Add(uploadResult.ExternalPostId!);

            _logger.LogInformation(
                "Uploaded unpublished photo {Index}/{Total}: {PhotoId} for post {PostId}",
                i + 1, mediaItems.Count, uploadResult.ExternalPostId, post.Id);
        }

        // HARD GUARD: Never call /feed without photo IDs
        if (photoIds.Count == 0)
        {
            _logger.LogError(
                "No photo IDs collected for post {PostId} despite {Count} media items. Aborting — will NOT create feed post.",
                post.Id, mediaItems.Count);
            return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                ErrorMessage: "No photos were uploaded successfully. Cannot create multi-photo post.");
        }

        _logger.LogInformation(
            "All {Count} photos uploaded for post {PostId}. Proceeding to create feed post with attached_media.",
            photoIds.Count, post.Id);

        // Step 2: Create feed post with attached_media
        var feedResult = await CreateFeedPostWithAttachedMediaAsync(
            pageId, post.Content, photoIds, accessToken, cancellationToken);

        if (!feedResult.Success)
        {
            _logger.LogWarning(
                "Failed to create feed post with attached_media for post {PostId}: {Error}",
                post.Id, feedResult.ErrorMessage);
        }
        else
        {
            _logger.LogInformation(
                "Created FB multi-photo feed post {ExternalId} for post {PostId}",
                feedResult.ExternalPostId, post.Id);
        }

        return feedResult;
    }

    /// <summary>
    /// Uploads a single photo as unpublished to a Facebook Page.
    /// POST /{pageId}/photos with published=false  (x-www-form-urlencoded).
    /// Returns the photo ID (media_fbid) for use in attached_media.
    /// </summary>
    private async Task<PublishResult> UploadUnpublishedPhotoAsync(
        string pageId, string imageUrl, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{pageId}/photos";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["url"] = imageUrl,
            ["published"] = "false",
            ["access_token"] = accessToken,
        });

        _logger.LogInformation(PostPilotLogEvents.OutboundCall, "FB_PHOTO_UPLOAD POST {Url}", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "FB_PHOTO_UPLOAD_RESPONSE {StatusCode}", response.StatusCode);
        // Log response with token redaction at debug only
        var redactedBody = RedactTokenInBody(responseBody);
        _logger.LogDebug("FB_PHOTO_UPLOAD_RESPONSE_BODY body={Body}", redactedBody);

        return ParseMetaPhotoIdResponse(response, responseBody);
    }

    /// <summary>
    /// Creates a feed post with attached_media referencing multiple unpublished photos.
    /// POST /{pageId}/feed with message + attached_media[0..N].
    /// MUST use x-www-form-urlencoded with UN-ENCODED brackets in keys.
    /// </summary>
    internal async Task<PublishResult> CreateFeedPostWithAttachedMediaAsync(
        string pageId, string message, List<string> photoIds,
        string accessToken, CancellationToken cancellationToken)
    {
        // HARD GUARD: refuse to call /feed without photo IDs
        if (photoIds == null || photoIds.Count == 0)
        {
            _logger.LogError("CreateFeedPostWithAttachedMediaAsync called with zero photoIds — aborting.");
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "Cannot create multi-photo feed post without any uploaded photos.");
        }

        var url = $"{_graphApiBaseUrl}/{pageId}/feed";

        // FB requires "message" to be non-empty even for photo-only posts (error 197).
        // Use a single space as safe fallback when the user provides no caption.
        var effectiveMessage = string.IsNullOrWhiteSpace(message) ? " " : message;

        // Build the form body MANUALLY so that brackets in keys are NOT percent-encoded.
        // FormUrlEncodedContent encodes [ ] as %5B %5D which Facebook may reject/ignore.
        var parts = new List<string>
        {
            $"message={Uri.EscapeDataString(effectiveMessage)}",
            $"access_token={Uri.EscapeDataString(accessToken)}",
        };

        for (int i = 0; i < photoIds.Count; i++)
        {
            // Each value must be a JSON string: {"media_fbid":"<id>"}
            var attachedMediaValue = $"{{\"media_fbid\":\"{photoIds[i]}\"}}";
            parts.Add($"attached_media[{i}]={Uri.EscapeDataString(attachedMediaValue)}");
        }

        var formBody = string.Join("&", parts);
        var content = new StringContent(formBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

        _logger.LogInformation(PostPilotLogEvents.OutboundCall,
            "FB_MULTIPHOTO_OUTBOUND POST {Url} photoCount={PhotoCount} hasMessage={HasMessage}",
            url, photoIds.Count, !string.IsNullOrWhiteSpace(message));

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "FB_MULTIPHOTO_RESPONSE {StatusCode}", response.StatusCode);
        // Log response with token redaction at debug only
        var redactedBody = RedactTokenInBody(responseBody);
        _logger.LogDebug("FB_MULTIPHOTO_RESPONSE_BODY body={Body}", redactedBody);

        return ParseMetaResponse(Guid.Empty, response, responseBody);
    }

    /// <summary>
    /// Resolves a public URL for a PostMediaItem (generates download URL if storage key).
    /// </summary>
    private string ResolveMediaUrlForItem(PostMediaItem item)
    {
        if (_mediaService.IsStorageKey(item.MediaUrl))
        {
            return _mediaService.GetPublishingUrl(item.MediaUrl, _metaDownloadUrlExpiration);
        }
        return item.MediaUrl;
    }

    private async Task<PublishResult> PublishVideoAsync(Post post, string pageId, string accessToken, CancellationToken cancellationToken)
    {
        // Generate download URL for Meta to fetch the video
        string videoUrl;
        if (_mediaService.IsStorageKey(post.MediaUrl!))
        {
            // Use longer expiration for videos since processing takes time
            videoUrl = _mediaService.GetPublishingUrl(post.MediaUrl!, _videoDownloadUrlExpiration);
            _logger.LogInformation("Generated publishing URL for video storage key {StorageKey} for post {PostId}",
                post.MediaUrl, post.Id);
        }
        else
        {
            videoUrl = post.MediaUrl!;
        }

        // Use the videos endpoint for video uploads
        var url = $"{_graphApiBaseUrl}/{pageId}/videos";

        // Build parameters dictionary
        var parameters = new Dictionary<string, string>
        {
            ["file_url"] = videoUrl,
            ["description"] = post.Content,
            ["access_token"] = accessToken
        };

        // Add thumbnail URL if available and feature is enabled
        if (_featureSettings.EnableFacebookThumbnail && !string.IsNullOrEmpty(post.SelectedThumbnailUrl))
        {
            var thumbnailUrl = GetThumbnailUrl(post.SelectedThumbnailUrl);
            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                parameters["thumb"] = thumbnailUrl;
                _logger.LogInformation("Including custom thumbnail URL for post {PostId}: {ThumbnailUrl}",
                    post.Id, thumbnailUrl);
            }
        }
        else if (!string.IsNullOrEmpty(post.SelectedThumbnailUrl))
        {
            _logger.LogDebug("Facebook thumbnail disabled by configuration. Thumbnail URL for post {PostId} will only be used in app UI.",
                post.Id);
        }

        var content = new FormUrlEncodedContent(parameters);

        _logger.LogInformation(PostPilotLogEvents.OutboundCall, "FB_VIDEO_OUTBOUND POST {Url} postId={PostId}", url, post.Id);
        _logger.LogDebug("FB_VIDEO_URL postId={PostId} videoUrl={VideoUrl}", post.Id, videoUrl);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        sw.Stop();
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation(PostPilotLogEvents.PublishAttempt,
            "FB_VIDEO_RESPONSE {StatusCode} postId={PostId} durationMs={DurationMs}",
            (int)response.StatusCode, post.Id, sw.ElapsedMilliseconds);
        _logger.LogDebug("FB_VIDEO_RESPONSE_BODY postId={PostId} body={Body}", post.Id, responseBody);

        return ParseMetaResponse(post.Id, response, responseBody);
    }

    private string? GetThumbnailUrl(string thumbnailUrl)
    {
        // If it's a storage key, route the thumbnail fetch through the API.
        if (_mediaService.IsStorageKey(thumbnailUrl))
        {
            return _mediaService.GetPublishingUrl(thumbnailUrl, _metaDownloadUrlExpiration);
        }

        // If it's a local API path, we can't use it directly with Meta
        // Meta needs a publicly accessible URL
        if (thumbnailUrl.Contains("/api/media/frames/"))
        {
            _logger.LogWarning("Local thumbnail URL cannot be used with Meta API: {Url}", thumbnailUrl);
            return null;
        }

        // Otherwise assume it's already a valid public URL
        return thumbnailUrl;
    }

    private PublishResult ParseMetaResponse(Guid postId, HttpResponseMessage response, string responseBody)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<MetaPostResponse>(responseBody);
            var externalId = result?.Id ?? result?.PostId;

            if (string.IsNullOrEmpty(externalId))
            {
                _logger.LogWarning("Meta API returned success but no post ID for {PostId}", postId);
            }

            return new PublishResult(true, ExternalPostId: externalId);
        }
        else
        {
            var error = JsonSerializer.Deserialize<MetaErrorResponse>(responseBody);
            var errorCode = error?.Error?.Code ?? 0;
            var errorType = ClassifyError(errorCode, error?.Error?.ErrorSubcode, error?.Error?.FbTraceId, error?.Error?.Message);

            _logger.LogWarning("Meta API error for post {PostId}: Code={Code}, Subcode={Subcode}, Message={Message}, FbTraceId={FbTraceId}",
                postId, errorCode, error?.Error?.ErrorSubcode, error?.Error?.Message, error?.Error?.FbTraceId);

            return new PublishResult(false,
                ErrorType: errorType,
                ErrorMessage: error?.Error?.Message ?? $"HTTP {(int)response.StatusCode}");
        }
    }

    /// <summary>
    /// Parses the response from uploading an unpublished photo.
    /// FB /photos returns {"id": "photo_id"} on success.
    /// </summary>
    private PublishResult ParseMetaPhotoIdResponse(HttpResponseMessage response, string responseBody)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<MetaPostResponse>(responseBody);
            var photoId = result?.Id;

            if (string.IsNullOrEmpty(photoId))
            {
                _logger.LogWarning("FB unpublished photo upload returned success but no photo ID");
                return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "FB photo upload returned success but no photo ID");
            }

            return new PublishResult(true, ExternalPostId: photoId);
        }
        else
        {
            var error = JsonSerializer.Deserialize<MetaErrorResponse>(responseBody);
            var errorCode = error?.Error?.Code ?? 0;
            var errorType = ClassifyError(errorCode, error?.Error?.ErrorSubcode, error?.Error?.FbTraceId, error?.Error?.Message);

            _logger.LogWarning("FB unpublished photo upload error: Code={Code}, Subcode={Subcode}, Message={Message}, FbTraceId={FbTraceId}",
                errorCode, error?.Error?.ErrorSubcode, error?.Error?.Message, error?.Error?.FbTraceId);

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
            "FB_PUBLISH_SUCCESS postId={PostId} externalPostId={ExternalPostId}",
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

        if (result.ErrorType == PublishErrorType.Permanent || post.RetryCount >= post.MaxRetries)
        {
            // Permanent failure or max retries exceeded
            post.Status = PostStatus.Failed;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(PostPilotLogEvents.PublishFail,
                "FB_PUBLISH_FAIL postId={PostId} retryCount={RetryCount} error={Error}",
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

        _logger.LogInformation(PostPilotLogEvents.RetryScheduled,
            "FB_RETRY_SCHEDULED postId={PostId} attempt={RetryCount}/{MaxRetries} retryAt={RetryAt} delayMin={DelayMinutes}",
            post.Id, post.RetryCount, post.MaxRetries, retryAt, delayMinutes);

        return result;
    }

    /// <summary>
    /// Redacts access tokens from response/request bodies for safe logging.
    /// </summary>
    private static string RedactTokenInBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return body;
        // Redact any access_token values in JSON or query string style
        return System.Text.RegularExpressions.Regex.Replace(
            body,
            @"(access_token["":\s=]+)[^\s&""}\]]+",
            "$1[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
