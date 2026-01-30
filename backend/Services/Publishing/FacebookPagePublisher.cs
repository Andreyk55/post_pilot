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
/// Publisher implementation for Facebook Pages using Meta Graph API.
/// </summary>
public class FacebookPagePublisher : IPostPublisher
{
    private readonly AppDbContext _dbContext;
    private readonly IPostScheduler _scheduler;
    private readonly IMediaService _mediaService;
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
        HttpClient httpClient,
        ILogger<FacebookPagePublisher> logger)
    {
        _dbContext = dbContext;
        _scheduler = scheduler;
        _mediaService = mediaService;
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

        // Check if we have a custom thumbnail to upload
        byte[]? thumbnailBytes = null;
        if (!string.IsNullOrEmpty(post.SelectedThumbnailUrl))
        {
            thumbnailBytes = await FetchThumbnailBytesAsync(post.SelectedThumbnailUrl, cancellationToken);
            if (thumbnailBytes != null)
            {
                _logger.LogInformation("Fetched thumbnail ({Size} bytes) for post {PostId}",
                    thumbnailBytes.Length, post.Id);
            }
        }

        HttpContent content;
        if (thumbnailBytes != null)
        {
            // Use multipart form data when we have a thumbnail to upload
            var multipartContent = new MultipartFormDataContent();
            multipartContent.Add(new StringContent(videoUrl), "file_url");
            multipartContent.Add(new StringContent(post.Content), "description");
            multipartContent.Add(new StringContent(accessToken), "access_token");

            // Add thumbnail as raw file data
            var thumbnailContent = new ByteArrayContent(thumbnailBytes);
            thumbnailContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            multipartContent.Add(thumbnailContent, "thumb", "thumbnail.jpg");

            content = multipartContent;
            _logger.LogInformation("Including custom thumbnail as multipart upload for post {PostId}", post.Id);
        }
        else
        {
            // Use form-urlencoded when no thumbnail
            content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["file_url"] = videoUrl,
                ["description"] = post.Content,
                ["access_token"] = accessToken
            });
        }

        _logger.LogInformation("Calling Meta Video API: POST {Url} for post {PostId}", url, post.Id);
        _logger.LogInformation("Video URL being sent to Meta: {VideoUrl}", videoUrl);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        _logger.LogInformation("Meta Video API response for post {PostId}: {StatusCode} - {Body}",
            post.Id, response.StatusCode, responseBody);

        return ParseMetaResponse(post.Id, response, responseBody);
    }

    private async Task<byte[]?> FetchThumbnailBytesAsync(string thumbnailUrl, CancellationToken cancellationToken)
    {
        try
        {
            // If it's a local file path (for frames stored locally)
            if (thumbnailUrl.Contains("/api/media/frames/"))
            {
                var filename = thumbnailUrl.Split("/api/media/frames/").Last();
                var framesDirectory = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "frames");
                var framePath = Path.Combine(framesDirectory, filename);

                if (File.Exists(framePath))
                {
                    return await File.ReadAllBytesAsync(framePath, cancellationToken);
                }
            }

            // Otherwise fetch from URL
            var response = await _httpClient.GetAsync(thumbnailUrl, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            _logger.LogWarning("Failed to fetch thumbnail from {Url}: {StatusCode}",
                thumbnailUrl, response.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching thumbnail from {Url}", thumbnailUrl);
            return null;
        }
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
