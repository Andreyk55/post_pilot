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
/// Publisher for Facebook Page Stories using Meta Graph API.
///
/// Photo story flow (two-step):
/// 1. Upload photo unpublished: POST /{page-id}/photos (url=..., published=false)
/// 2. Create story: POST /{page-id}/photo_stories (photo_id=...)
///
/// Video story flow (rupload protocol):
/// 1. Start:  POST /{page-id}/video_stories  upload_phase=start, file_size=N
///            → returns video_id + upload_url
/// 2. Upload: POST {upload_url}  with raw binary body + headers (Authorization, offset, file_size)
/// 3. Finish: POST /{page-id}/video_stories  upload_phase=finish, video_id
///
/// The FacebookStoryMediaId field stores "videoId|uploadUrl" for idempotency:
/// if start succeeded, retries skip straight to upload/finish.
/// </summary>
public class FacebookStoryPublisher : IStoryPublisher
{
    private readonly AppDbContext _dbContext;
    private readonly IPostScheduler _scheduler;
    private readonly IMediaService _mediaService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<FacebookStoryPublisher> _logger;
    private readonly string _graphApiBaseUrl;
    private readonly TimeSpan _mediaDownloadUrlExpiration;
    private readonly TimeSpan _videoDownloadUrlExpiration;

    // Meta error codes — transient (retry)
    private static readonly HashSet<int> TransientErrorCodes = new()
    {
        1,    // Unknown error
        2,    // Service temporarily unavailable
        4,    // Too many calls
        17,   // User request limit reached
        341,  // Temporarily blocked
        368,  // Temporarily blocked for policies violation
        506   // Duplicate post
    };

    // Meta error codes — permanent (don't retry)
    private static readonly HashSet<int> PermanentErrorCodes = new()
    {
        10,   // Permission denied
        100,  // Invalid parameter
        102,  // Session invalidated
        197,  // Empty post
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

    public FacebookStoryPublisher(
        AppDbContext dbContext,
        IPostScheduler scheduler,
        IMediaService mediaService,
        HttpClient httpClient,
        ILogger<FacebookStoryPublisher> logger,
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
        _videoDownloadUrlExpiration = TimeSpan.FromMinutes(publishingOptions.VideoDownloadUrlExpirationMinutes);
    }

    public async Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Facebook story publish for post {PostId}", postId);

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
        if (post.TargetPage == null || string.IsNullOrEmpty(post.TargetPage.AccessToken))
        {
            await MarkFailedAsync(post, "No target page or access token configured", cancellationToken);
            return new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                ErrorMessage: "No target page configured");
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

        // Step 6: Route to photo or video story flow
        try
        {
            PublishResult result;

            if (post.MediaType == MediaType.Video)
            {
                result = await PublishVideoStoryAsync(post, cancellationToken);
            }
            else
            {
                result = await PublishPhotoStoryAsync(post, cancellationToken);
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
            _logger.LogError(ex, "Network error publishing Facebook story {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Network error: {ex.Message}"),
                cancellationToken);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout publishing Facebook story {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: "Request timed out"),
                cancellationToken);
        }
        catch (Exception ex) when (IsTransientException(ex))
        {
            _logger.LogError(ex, "Transient error publishing Facebook story {PostId}", postId);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"Transient error: {ex.Message}"),
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Internal error (non-retryable) publishing Facebook story {PostId}: {ExceptionType}", postId, ex.GetType().Name);
            return await HandlePublishFailureAsync(post,
                new PublishResult(false, ErrorType: PublishErrorType.Permanent,
                    ErrorMessage: $"Internal error (non-retryable): {ex.GetType().Name}: {ex.Message}"),
                cancellationToken);
        }
    }

    // ──────────────────────────────────────────────
    //  PHOTO STORY FLOW
    // ──────────────────────────────────────────────

    private async Task<PublishResult> PublishPhotoStoryAsync(
        Post post, CancellationToken cancellationToken)
    {
        var pageId = post.TargetPage!.PageId;
        var accessToken = post.TargetPage.AccessToken;

        // Step 1: Upload photo as unpublished (idempotent via FacebookStoryMediaId)
        if (string.IsNullOrEmpty(post.FacebookStoryMediaId))
        {
            string imageUrl;
            if (_mediaService.IsStorageKey(post.MediaUrl!))
            {
                imageUrl = _mediaService.GenerateDownloadUrl(post.MediaUrl!, _mediaDownloadUrlExpiration);
                _logger.LogInformation("Generated download URL for storage key {StorageKey} for FB story {PostId}",
                    post.MediaUrl, post.Id);
            }
            else
            {
                imageUrl = post.MediaUrl!;
            }

            var uploadResult = await UploadUnpublishedPhotoAsync(
                pageId, imageUrl, accessToken, cancellationToken);

            if (!uploadResult.Success)
                return uploadResult;

            post.FacebookStoryMediaId = uploadResult.ExternalPostId!;
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Uploaded unpublished photo {PhotoId} for FB story {PostId}",
                post.FacebookStoryMediaId, post.Id);
        }

        // Step 2: Create photo story
        return await CreatePhotoStoryAsync(
            pageId, post.FacebookStoryMediaId!, accessToken, cancellationToken);
    }

    // ──────────────────────────────────────────────
    //  VIDEO STORY FLOW  (Resumable Upload Protocol)
    // ──────────────────────────────────────────────

    private async Task<PublishResult> PublishVideoStoryAsync(
        Post post, CancellationToken cancellationToken)
    {
        var pageId = post.TargetPage!.PageId;
        var accessToken = post.TargetPage.AccessToken;

        // ── Download video bytes from storage provider or external URL ──
        string videoUrl;
        if (_mediaService.IsStorageKey(post.MediaUrl!))
        {
            videoUrl = _mediaService.GenerateDownloadUrl(post.MediaUrl!, _videoDownloadUrlExpiration);
            _logger.LogInformation("Generated download URL for video storage key {StorageKey} for FB story {PostId}",
                post.MediaUrl, post.Id);
        }
        else
        {
            videoUrl = post.MediaUrl!;
        }

        _logger.LogInformation("Downloading video for FB story {PostId}...", post.Id);
        var videoBytes = await _httpClient.GetByteArrayAsync(videoUrl, cancellationToken);
        _logger.LogInformation("Downloaded video ({Size} bytes) for FB story {PostId}",
            videoBytes.Length, post.Id);

        // ── Phase 1: Start (idempotent via FacebookStoryMediaId) ──
        string uploadUrlStr;
        string videoId;

        if (string.IsNullOrEmpty(post.FacebookStoryMediaId))
        {
            var startResult = await VideoStoryStartAsync(
                pageId, videoBytes.Length, accessToken, cancellationToken);

            if (!startResult.Success)
                return new PublishResult(false, ErrorType: startResult.ErrorType,
                    ErrorMessage: startResult.ErrorMessage);

            // FacebookStoryMediaId stores "videoId|uploadUrl" for retry
            videoId = startResult.VideoId!;
            uploadUrlStr = startResult.UploadUrl!;
            post.FacebookStoryMediaId = $"{videoId}|{uploadUrlStr}";
            post.UpdatedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "FB video story start phase complete: video={VideoId}, uploadUrl={UploadUrl} for post {PostId}",
                videoId, uploadUrlStr, post.Id);
        }
        else
        {
            // Retry path — start phase was already completed
            var parts = post.FacebookStoryMediaId.Split('|', 2);
            videoId = parts[0];
            uploadUrlStr = parts.Length > 1 ? parts[1] : "";
            _logger.LogInformation(
                "Resuming FB video story upload: video={VideoId} for post {PostId}",
                videoId, post.Id);
        }

        // ── Phase 2: Upload binary to the rupload URL ──
        var uploadResult = await VideoStoryUploadAsync(
            uploadUrlStr, videoBytes, accessToken, cancellationToken);

        if (!uploadResult.Success)
            return uploadResult;

        // ── Phase 3: Finish ──
        return await VideoStoryFinishAsync(
            pageId, videoId, accessToken, cancellationToken);
    }

    // ──────────────────────────────────────────────
    //  GRAPH API METHODS
    // ──────────────────────────────────────────────

    /// <summary>
    /// POST /{page-id}/photos with published=false to upload photo without posting to feed.
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

        _logger.LogInformation("Uploading unpublished photo for FB story: POST {Url}", url);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("FB photo upload response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaIdResponse(response, responseBody, "unpublished photo upload");
    }

    /// <summary>
    /// POST /{page-id}/photo_stories with photo_id to create the story.
    /// </summary>
    private async Task<PublishResult> CreatePhotoStoryAsync(
        string pageId, string photoId, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{pageId}/photo_stories";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["photo_id"] = photoId,
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("Creating FB photo story: POST {Url} photo_id={PhotoId}", url, photoId);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("FB photo story response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        return ParseMetaStoryResponse(response, responseBody, "photo story creation");
    }

    /// <summary>
    /// Phase 1 — Start: POST /{page-id}/video_stories with upload_phase=start.
    /// Returns video_id and upload_url (rupload endpoint).
    /// </summary>
    private async Task<VideoStoryStartResult> VideoStoryStartAsync(
        string pageId, long fileSize, string accessToken, CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{pageId}/video_stories";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["upload_phase"] = "start",
            ["file_size"] = fileSize.ToString(),
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("FB video story START: POST {Url} file_size={FileSize}", url, fileSize);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("FB video story start response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        if (!response.IsSuccessStatusCode)
        {
            var errorResult = ParseMetaError(response, responseBody, "video story start");
            return new VideoStoryStartResult(false, ErrorType: errorResult.ErrorType,
                ErrorMessage: errorResult.ErrorMessage);
        }

        var result = JsonSerializer.Deserialize<FbVideoStartResponse>(responseBody);

        if (string.IsNullOrEmpty(result?.VideoId))
        {
            _logger.LogWarning("FB video story start returned no video_id");
            return new VideoStoryStartResult(false, ErrorType: PublishErrorType.Transient,
                ErrorMessage: "FB video story start returned no video_id");
        }

        if (string.IsNullOrEmpty(result?.UploadUrl))
        {
            _logger.LogWarning("FB video story start returned no upload_url");
            return new VideoStoryStartResult(false, ErrorType: PublishErrorType.Transient,
                ErrorMessage: "FB video story start returned no upload_url");
        }

        return new VideoStoryStartResult(true,
            VideoId: result.VideoId,
            UploadUrl: result.UploadUrl);
    }

    /// <summary>
    /// Phase 2 — Upload: POST binary video data to the rupload URL returned by the start phase.
    /// Uses headers: Authorization (OAuth token), offset, file_size.
    /// </summary>
    private async Task<PublishResult> VideoStoryUploadAsync(
        string uploadUrl, byte[] videoBytes, string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        request.Headers.Add("Authorization", $"OAuth {accessToken}");
        request.Headers.Add("offset", "0");
        request.Headers.Add("file_size", videoBytes.Length.ToString());

        request.Content = new ByteArrayContent(videoBytes);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        _logger.LogInformation(
            "FB video story UPLOAD: POST {Url} file_size={FileSize}",
            uploadUrl, videoBytes.Length);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("FB video story upload response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("FB video story upload failed: {StatusCode} - {Body}",
                response.StatusCode, RedactToken(responseBody));
            return ParseMetaError(response, responseBody, "video story upload (rupload)");
        }

        // rupload returns {"success":true,"h":"..."} on success
        _logger.LogInformation("FB video story binary upload complete");
        return new PublishResult(true);
    }

    /// <summary>
    /// Phase 3 — Finish: POST /{page-id}/video_stories with upload_phase=finish + video_id.
    /// Returns the story post ID on success.
    /// </summary>
    private async Task<PublishResult> VideoStoryFinishAsync(
        string pageId, string videoId, string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{_graphApiBaseUrl}/{pageId}/video_stories";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["upload_phase"] = "finish",
            ["video_id"] = videoId,
            ["access_token"] = accessToken,
        });

        _logger.LogInformation("FB video story FINISH: video={VideoId}", videoId);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("FB video story finish response: {StatusCode} - {Body}",
            response.StatusCode, RedactToken(responseBody));

        if (!response.IsSuccessStatusCode)
            return ParseMetaError(response, responseBody, "video story finish");

        // Finish returns { "success": true } — use video_id as the external post ID
        var result = JsonSerializer.Deserialize<FbMetaResponse>(responseBody);
        var externalId = result?.PostId ?? result?.Id ?? videoId;

        return new PublishResult(true, ExternalPostId: externalId);
    }

    // ──────────────────────────────────────────────
    //  HELPERS
    // ──────────────────────────────────────────────

    private PublishResult ParseMetaIdResponse(
        HttpResponseMessage response, string responseBody, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<FbMetaResponse>(responseBody);
            var id = result?.Id;

            if (string.IsNullOrEmpty(id))
            {
                _logger.LogWarning("FB {Operation} returned success but no ID", operation);
                return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"FB {operation} returned success but no ID");
            }

            return new PublishResult(true, ExternalPostId: id);
        }
        else
        {
            return ParseMetaError(response, responseBody, operation);
        }
    }

    private PublishResult ParseMetaStoryResponse(
        HttpResponseMessage response, string responseBody, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<FbMetaResponse>(responseBody);
            // photo_stories/video_stories may return "id" or "post_id"
            var id = result?.Id ?? result?.PostId;

            if (string.IsNullOrEmpty(id))
            {
                _logger.LogWarning("FB {Operation} returned success but no ID", operation);
                return new PublishResult(false, ErrorType: PublishErrorType.Transient,
                    ErrorMessage: $"FB {operation} returned success but no ID");
            }

            return new PublishResult(true, ExternalPostId: id);
        }
        else
        {
            return ParseMetaError(response, responseBody, operation);
        }
    }

    private PublishResult ParseMetaError(
        HttpResponseMessage response, string responseBody, string operation)
    {
        var error = JsonSerializer.Deserialize<FbMetaErrorResponse>(responseBody);
        var errorCode = error?.Error?.Code ?? 0;
        var errorType = ClassifyError(errorCode, error?.Error?.ErrorSubcode, error?.Error?.FbTraceId, error?.Error?.Message);

        _logger.LogWarning("FB {Operation} error: Code={Code}, Subcode={Subcode}, Message={Message}, FbTraceId={FbTraceId}",
            operation, errorCode, error?.Error?.ErrorSubcode, error?.Error?.Message, error?.Error?.FbTraceId);

        return new PublishResult(false,
            ErrorType: errorType,
            ErrorMessage: error?.Error?.Message ?? $"HTTP {(int)response.StatusCode}");
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

        // Fetch permalink_url for Facebook stories
        var permalinkUrl = await FetchStoryPermalinkAsync(
            externalPostId, post.TargetPage!.PageId, post.TargetPage.AccessToken, cancellationToken);
        if (!string.IsNullOrEmpty(permalinkUrl))
        {
            post.ExternalPostUrl = permalinkUrl;
            _logger.LogInformation("Fetched permalink_url for FB story {PostId}: {PermalinkUrl}",
                post.Id, permalinkUrl);
        }
        else
        {
            _logger.LogWarning("Could not fetch permalink_url for FB story {PostId}", post.Id);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Facebook story {PostId} published successfully as {ExternalPostId}",
            post.Id, externalPostId);
    }

    private async Task MarkFailedAsync(Post post, string errorMessage,
        CancellationToken cancellationToken)
    {
        post.Status = PostStatus.Failed;
        post.ErrorMessage = errorMessage;
        post.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogWarning("Facebook story {PostId} failed permanently: {Error}", post.Id, errorMessage);
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
                "Facebook story {PostId} failed permanently after {RetryCount} attempts: {Error}",
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
            "Facebook story {PostId} scheduled for retry #{RetryCount} at {RetryAt}",
            post.Id, post.RetryCount, retryAt);

        return result;
    }

    /// <summary>
    /// Fetches the permalink_url for a published Facebook story.
    /// Tries GET /{id}?fields=permalink_url first, then falls back to GET /{pageId}_{id}?fields=permalink_url.
    /// </summary>
    private async Task<string?> FetchStoryPermalinkAsync(
        string storyId, string pageId, string accessToken, CancellationToken cancellationToken)
    {
        // Try 1: Direct story ID
        var url1 = $"{_graphApiBaseUrl}/{storyId}?fields=permalink_url&access_token={accessToken}";

        try
        {
            var response = await _httpClient.GetAsync(url1, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<FbPermalinkResponse>(responseBody);
                if (!string.IsNullOrEmpty(result?.PermalinkUrl))
                {
                    _logger.LogInformation("Fetched permalink_url using direct ID {StoryId}", storyId);
                    return result.PermalinkUrl;
                }
            }

            _logger.LogDebug("Direct ID fetch failed for {StoryId}: {StatusCode} - {Body}",
                storyId, response.StatusCode, RedactToken(responseBody));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception fetching permalink with direct ID {StoryId}", storyId);
        }

        // Try 2: pageId_storyId format
        var compositeId = $"{pageId}_{storyId}";
        var url2 = $"{_graphApiBaseUrl}/{compositeId}?fields=permalink_url&access_token={accessToken}";

        try
        {
            var response = await _httpClient.GetAsync(url2, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var result = JsonSerializer.Deserialize<FbPermalinkResponse>(responseBody);
                if (!string.IsNullOrEmpty(result?.PermalinkUrl))
                {
                    _logger.LogInformation("Fetched permalink_url using composite ID {CompositeId}", compositeId);
                    return result.PermalinkUrl;
                }
            }

            _logger.LogWarning("Composite ID fetch failed for {CompositeId}: {StatusCode} - {Body}",
                compositeId, response.StatusCode, RedactToken(responseBody));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception fetching permalink with composite ID {CompositeId}", compositeId);
        }

        return null;
    }

    private static string RedactToken(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(access_token["":\s=]+)[^\s&""}\]]+",
            "$1[REDACTED]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}

// Response models for Facebook Story API (local to this file)

internal class FbMetaResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("post_id")]
    public string? PostId { get; set; }

    [JsonPropertyName("success")]
    public bool? Success { get; set; }
}

internal class FbVideoStartResponse
{
    [JsonPropertyName("video_id")]
    public string? VideoId { get; set; }

    [JsonPropertyName("upload_url")]
    public string? UploadUrl { get; set; }
}

/// <summary>
/// Result record for the video story start phase, carrying the video_id
/// and rupload URL alongside standard error fields.
/// </summary>
internal record VideoStoryStartResult(
    bool Success,
    string? ExternalPostId = null,
    PublishErrorType? ErrorType = null,
    string? ErrorMessage = null,
    string? VideoId = null,
    string? UploadUrl = null);

internal class FbMetaErrorResponse
{
    [JsonPropertyName("error")]
    public FbMetaError? Error { get; set; }
}

internal class FbMetaError
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

internal class FbPermalinkResponse
{
    [JsonPropertyName("permalink_url")]
    public string? PermalinkUrl { get; set; }
}
