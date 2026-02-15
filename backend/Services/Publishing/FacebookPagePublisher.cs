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

    private const string GraphApiBaseUrl = "https://graph.facebook.com/v21.0";
    private static readonly TimeSpan MetaDownloadUrlExpiration = TimeSpan.FromHours(1);

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
        ILogger<FacebookPagePublisher> logger)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _mediaService = mediaService;
        _featureSettings = featureSettings;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting publish for post {PostId}", postId);

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
            if (_mediaService.IsS3Key(post.MediaUrl))
            {
                imageUrl = _mediaService.GenerateDownloadUrl(post.MediaUrl, MetaDownloadUrlExpiration);
                _logger.LogInformation("Generated pre-signed URL for S3 key {S3Key} for post {PostId}",
                    post.MediaUrl, post.Id);
            }
            else
            {
                imageUrl = post.MediaUrl;
            }

            url = $"{GraphApiBaseUrl}/{pageId}/photos";
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
            if (_mediaService.IsS3Key(post.MediaUrl))
            {
                imageUrl = _mediaService.GenerateDownloadUrl(post.MediaUrl, MetaDownloadUrlExpiration);
                _logger.LogInformation("Generated pre-signed URL for S3 key {S3Key} for post {PostId}",
                    post.MediaUrl, post.Id);
            }
            else
            {
                imageUrl = post.MediaUrl;
            }

            url = $"{GraphApiBaseUrl}/{pageId}/photos";
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

        _logger.LogInformation("Meta API response for post {PostId}: {StatusCode} - {Body}",
            post.Id, response.StatusCode, responseBody);

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
        var url = $"{GraphApiBaseUrl}/{pageId}/photos";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["url"] = imageUrl,
            ["published"] = "false",
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("Uploading unpublished photo: POST {Url} (imageUrl={ImageUrl})",
            url, imageUrl);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // Log response with token redaction
        var redactedBody = RedactTokenInBody(responseBody);
        _logger.LogInformation("Unpublished photo upload response: {StatusCode} - {Body}",
            response.StatusCode, redactedBody);

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

        var url = $"{GraphApiBaseUrl}/{pageId}/feed";

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

        // Log attached_media keys and photo count (no tokens)
        var attachedKeys = string.Join(", ", Enumerable.Range(0, photoIds.Count).Select(i => $"attached_media[{i}]"));
        _logger.LogInformation(
            "Creating FB feed post: POST {Url} with {PhotoCount} photos, keys=[{AttachedKeys}], hasUserMessage={HasMessage}",
            url, photoIds.Count, attachedKeys, !string.IsNullOrWhiteSpace(message));

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // Log response with token redaction
        var redactedBody = RedactTokenInBody(responseBody);
        _logger.LogInformation("FB feed post response: {StatusCode} - {Body}",
            response.StatusCode, redactedBody);

        return ParseMetaResponse(Guid.Empty, response, responseBody);
    }

    /// <summary>
    /// Resolves a public URL for a PostMediaItem (generates pre-signed URL if S3 key).
    /// </summary>
    private string ResolveMediaUrlForItem(PostMediaItem item)
    {
        if (_mediaService.IsS3Key(item.MediaUrl))
        {
            return _mediaService.GenerateDownloadUrl(item.MediaUrl, MetaDownloadUrlExpiration);
        }
        return item.MediaUrl;
    }

    private async Task<PublishResult> PublishVideoAsync(Post post, string pageId, string accessToken, CancellationToken cancellationToken)
    {
        // Generate download URL for Meta to fetch the video
        string videoUrl;
        if (_mediaService.IsS3Key(post.MediaUrl!))
        {
            // Use longer expiration for videos since processing takes time
            videoUrl = _mediaService.GenerateDownloadUrl(post.MediaUrl!, TimeSpan.FromHours(2));
            _logger.LogInformation("Generated pre-signed URL for video S3 key {S3Key} for post {PostId}",
                post.MediaUrl, post.Id);
        }
        else
        {
            videoUrl = post.MediaUrl!;
        }

        // Use the videos endpoint for video uploads
        var url = $"{GraphApiBaseUrl}/{pageId}/videos";

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

        _logger.LogInformation("Calling Meta Video API: POST {Url} for post {PostId}", url, post.Id);
        _logger.LogInformation("Video URL being sent to Meta: {VideoUrl}", videoUrl);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("Meta Video API response for post {PostId}: {StatusCode} - {Body}",
            post.Id, response.StatusCode, responseBody);

        return ParseMetaResponse(post.Id, response, responseBody);
    }

    private string? GetThumbnailUrl(string thumbnailUrl)
    {
        // If it's an S3 key, generate a pre-signed URL
        if (_mediaService.IsS3Key(thumbnailUrl))
        {
            return _mediaService.GenerateDownloadUrl(thumbnailUrl, MetaDownloadUrlExpiration);
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
            var errorType = ClassifyError(errorCode);

            _logger.LogWarning("Meta API error for post {PostId}: Code={Code}, Message={Message}",
                postId, errorCode, error?.Error?.Message);

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
            var errorType = ClassifyError(errorCode);

            _logger.LogWarning("FB unpublished photo upload error: Code={Code}, Message={Message}",
                errorCode, error?.Error?.Message);

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
