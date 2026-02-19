using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PostsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPostScheduler _scheduler;
    private readonly IFacebookInsightsService _facebookInsights;
    private readonly ILogger<PostsController> _logger;

    public PostsController(
        AppDbContext context,
        IPostScheduler scheduler,
        IFacebookInsightsService facebookInsights,
        ILogger<PostsController> logger)
    {
        _context = context;
        _scheduler = scheduler;
        _facebookInsights = facebookInsights;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<PostDto>>> GetPosts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] PostStatus? status = null,
        [FromQuery] PostType? postType = null)
    {
        // Ensure valid pagination parameters
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);

        var query = _context.Posts.AsQueryable();

        // Apply status filter if provided
        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        // Apply post type filter if provided
        if (postType.HasValue)
        {
            query = query.Where(p => p.PostType == postType.Value);
        }

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var posts = await query
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .OrderByDescending(p => p.ScheduledAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => PostDto.FromEntity(p))
            .ToListAsync();

        return new PaginatedResponse<PostDto>(
            posts,
            page,
            pageSize,
            totalCount,
            totalPages
        );
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PostDto>> GetPost(Guid id)
    {
        var post = await _context.Posts
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null)
        {
            return NotFound();
        }

        return PostDto.FromEntity(post);
    }

    [HttpGet("{id}/details")]
    public async Task<ActionResult<PostDetailsDto>> GetPostDetails(Guid id, CancellationToken cancellationToken)
    {
        var post = await _context.Posts
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (post == null)
        {
            return NotFound();
        }

        // Fetch engagement metrics for published Facebook posts
        PostEngagementDto? engagement = null;
        string? externalPostUrl = post.ExternalPostUrl; // Use stored permalink (e.g. from Instagram, FB stories)
        string? profileUrl = post.ProfileUrl;
        string? pageUrl = null;

        // Compute profileUrl for Instagram stories (fallback when story permalink not available)
        if (post.Platform == Platform.Instagram &&
            post.PostType == PostType.Story &&
            post.TargetInstagramAccount != null &&
            string.IsNullOrEmpty(profileUrl))
        {
            profileUrl = $"https://www.instagram.com/{post.TargetInstagramAccount.Username}/";
        }

        // Compute pageUrl for Facebook posts/stories (fallback for stories when permalink unavailable)
        if (post.Platform == Platform.Facebook && post.TargetPage != null)
        {
            pageUrl = $"https://www.facebook.com/{post.TargetPage.PageId}";
        }

        if (post.Platform == Platform.Facebook &&
            post.Status == PostStatus.Published &&
            !string.IsNullOrEmpty(post.ExternalPostId))
        {
            // For FB stories, use the stored ExternalPostUrl (permalink_url fetched after publish)
            // For FB feed posts, construct the URL if not already stored
            if (post.PostType == PostType.Feed && string.IsNullOrEmpty(externalPostUrl))
            {
                externalPostUrl = $"https://www.facebook.com/{post.ExternalPostId}";
            }

            // Try to get page access token - first from TargetPage, then look up by Facebook PageId
            string? pageAccessToken = post.TargetPage?.AccessToken;

            if (string.IsNullOrEmpty(pageAccessToken))
            {
                // TargetPage might be null if user reconnected Meta (new ConnectedPage IDs)
                // Try to find the page by Facebook PageId from ExternalPostId (format: pageId_postId)
                var externalIdParts = post.ExternalPostId.Split('_');
                if (externalIdParts.Length >= 2)
                {
                    // Standard format: pageId_postId
                    var facebookPageId = externalIdParts[0];
                    _logger.LogInformation(
                        "Looking up page by Facebook PageId {FacebookPageId}",
                        facebookPageId);

                    var currentPage = await _context.Set<ConnectedPage>()
                        .FirstOrDefaultAsync(p => p.PageId == facebookPageId, cancellationToken);

                    pageAccessToken = currentPage?.AccessToken;

                    if (currentPage != null)
                    {
                        _logger.LogInformation("Found page {PageName} for engagement fetch", currentPage.Name);
                    }
                }

                // If still no token (photo posts may not have pageId_postId format),
                // fall back to the user's first connected page
                if (string.IsNullOrEmpty(pageAccessToken))
                {
                    _logger.LogInformation(
                        "ExternalPostId format not standard, falling back to first connected page");

                    var firstPage = await _context.Set<ConnectedPage>()
                        .FirstOrDefaultAsync(cancellationToken);

                    pageAccessToken = firstPage?.AccessToken;

                    if (firstPage != null)
                    {
                        _logger.LogInformation("Using page {PageName} for engagement fetch", firstPage.Name);
                    }
                }
            }

            if (!string.IsNullOrEmpty(pageAccessToken))
            {
                _logger.LogInformation("Fetching engagement for post {PostId}", post.Id);

                engagement = await _facebookInsights.GetPostEngagementAsync(
                    post.ExternalPostId,
                    pageAccessToken,
                    cancellationToken);
            }
            else
            {
                _logger.LogWarning(
                    "Cannot fetch engagement for post {PostId}: no page access token available",
                    post.Id);
            }
        }

        return new PostDetailsDto(
            Id: post.Id,
            Content: post.Content,
            MediaUrl: post.MediaUrl,
            MediaType: post.MediaType.ToString(),
            PostType: post.PostType.ToString(),
            Platform: post.Platform.ToString(),
            ScheduledAt: post.ScheduledAt,
            Status: post.Status.ToString(),
            CreatedAt: post.CreatedAt,
            UpdatedAt: post.UpdatedAt,
            TargetPageId: post.TargetPageId,
            TargetPageName: post.TargetPage?.Name,
            TargetInstagramAccountId: post.TargetInstagramAccountId,
            TargetInstagramAccountName: post.TargetInstagramAccount != null
                ? $"@{post.TargetInstagramAccount.Username}"
                : null,
            PublishedAt: post.PublishedAt,
            ExternalPostId: post.ExternalPostId,
            ErrorMessage: post.ErrorMessage,
            RetryCount: post.RetryCount,
            Engagement: engagement,
            ExternalPostUrl: externalPostUrl,
            ProfileUrl: profileUrl,
            PageUrl: pageUrl,
            InstagramMediaType: post.InstagramMediaType?.ToString(),
            MediaItems: post.MediaItems?.Count > 0
                ? post.MediaItems.OrderBy(m => m.Order)
                    .Select(m => new PostDetailsMediaItemDto(m.Id, m.Order, m.MediaUrl, m.MediaType.ToString()))
                    .ToList()
                : null
        );
    }

    [HttpPost]
    public async Task<ActionResult<PostDto>> CreatePost(CreatePostRequest request)
    {
        var validationErrors = ValidateCreatePostRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // For Facebook posts, verify the target page is connected and has a valid token
        if (request.Platform == Platform.Facebook)
        {
            if (!request.TargetPageId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "A Facebook Page must be selected to schedule a Facebook post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var targetPage = await _context.Set<ConnectedPage>()
                .FirstOrDefaultAsync(p => p.Id == request.TargetPageId.Value);

            if (targetPage == null)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration disconnected",
                    Detail = "The selected Facebook Page is no longer connected. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            if (string.IsNullOrEmpty(targetPage.AccessToken))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration token missing",
                    Detail = "The selected Facebook Page's access token is missing. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            // Facebook multi-photo validation: 2-10 images via MediaItems
            if (request.MediaItems is { Count: > 0 })
            {
                if (request.MediaItems.Count < 2 || request.MediaItems.Count > 10)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid multi-photo post",
                        Detail = "Facebook multi-photo posts require 2 to 10 images.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }

                // All items must be images (no videos in multi-photo)
                if (request.MediaItems.Any(m => m.MediaType != MediaType.Image))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid multi-photo media",
                        Detail = "Facebook multi-photo posts only support images. Videos are not allowed in multi-photo posts.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
            }
        }

        // Instagram-specific validation
        if (request.Platform == Platform.Instagram)
        {
            if (!request.TargetInstagramAccountId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "An Instagram Business Account must be selected to schedule an Instagram post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var targetIgAccount = await _context.Set<ConnectedInstagramAccount>()
                .FirstOrDefaultAsync(a => a.Id == request.TargetInstagramAccountId.Value);

            if (targetIgAccount == null)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration disconnected",
                    Detail = "The selected Instagram account is no longer connected. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            // Instagram carousel: 2-10 images via MediaItems
            var hasMultipleMediaItems = request.MediaItems is { Count: > 0 };
            if (hasMultipleMediaItems)
            {
                // Carousel validation
                if (request.MediaItems!.Count < 2 || request.MediaItems.Count > 10)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid carousel",
                        Detail = "Instagram carousel requires 2 to 10 images.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }

                // All items must be images
                if (request.MediaItems.Any(m => m.MediaType != MediaType.Image))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid carousel media",
                        Detail = "Instagram carousel only supports images. Videos are not allowed in carousel posts.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
            }
            else
            {
                // Single media: existing validation (image or video required)
                var mediaType = request.MediaType ?? MediaType.None;
                if (string.IsNullOrEmpty(request.MediaUrl) || (mediaType != MediaType.Image && mediaType != MediaType.Video))
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Invalid media",
                        Detail = "Instagram Feed posts require at least one media item (image or video). Text-only posts are not supported.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
            }
        }

        // Story-specific validation
        if (request.PostType == PostType.Story)
        {
            // Stories require media (no text-only stories)
            var storyMediaType = request.MediaType ?? MediaType.None;
            if (string.IsNullOrEmpty(request.MediaUrl) || (storyMediaType != MediaType.Image && storyMediaType != MediaType.Video))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid story",
                    Detail = "Stories require exactly one media item (image or video).",
                    Status = StatusCodes.Status400BadRequest,
                });
            }

            // Stories don't support carousel/multi-media
            if (request.MediaItems is { Count: > 0 })
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid story",
                    Detail = "Stories only support a single media item. Multi-image stories are not supported.",
                    Status = StatusCodes.Status400BadRequest,
                });
            }

            // Stories only supported on Facebook and Instagram
            if (request.Platform != Platform.Facebook && request.Platform != Platform.Instagram)
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Unsupported platform",
                    Detail = "Stories are only supported on Facebook and Instagram.",
                    Status = StatusCodes.Status400BadRequest,
                });
            }
        }

        // Note: Media validation is done client-side via POST /api/media/validate before submission.
        // The frontend blocks submission if media is invalid.

        var post = new Post
        {
            Id = Guid.NewGuid(),
            // Stories don't support captions — ignore any content sent by the client
            Content = request.PostType == PostType.Story ? string.Empty : (request.Content ?? string.Empty),
            MediaUrl = request.MediaUrl,
            MediaType = request.MediaType ?? MediaType.None,
            PostType = request.PostType,
            Platform = request.Platform,
            ScheduledAt = request.ScheduledAt,
            TargetPageId = request.TargetPageId,
            TargetInstagramAccountId = request.TargetInstagramAccountId,
            SelectedThumbnailUrl = request.SelectedThumbnailUrl,
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Add media items for carousel posts
        if (request.MediaItems is { Count: > 0 })
        {
            post.MediaType = MediaType.Image; // Carousel is image-based
            post.MediaUrl = request.MediaItems.OrderBy(m => m.Order).First().MediaUrl; // First image as legacy preview
            post.MediaItems = request.MediaItems
                .OrderBy(m => m.Order)
                .Select((m, i) => new PostMediaItem
                {
                    Id = Guid.NewGuid(),
                    PostId = post.Id,
                    Order = i,
                    MediaUrl = m.MediaUrl,
                    MediaType = m.MediaType,
                })
                .ToList();
        }

        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        // Schedule the post for publication
        var scheduleResult = await _scheduler.ScheduleAsync(post);
        if (scheduleResult.Success && !string.IsNullOrEmpty(scheduleResult.ScheduleIdentifier))
        {
            post.ScheduleArn = scheduleResult.ScheduleIdentifier;
            await _context.SaveChangesAsync();
        }
        else if (!scheduleResult.Success)
        {
            _logger.LogWarning("Failed to schedule post {PostId}: {Error}",
                post.Id, scheduleResult.ErrorMessage);
        }

        // Reload navigation properties for the response
        await _context.Entry(post).Reference(p => p.TargetPage).LoadAsync();
        await _context.Entry(post).Reference(p => p.TargetInstagramAccount).LoadAsync();
        await _context.Entry(post).Collection(p => p.MediaItems).LoadAsync();

        return CreatedAtAction(nameof(GetPost), new { id = post.Id }, PostDto.FromEntity(post));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePost(Guid id, UpdatePostRequest request)
    {
        var post = await _context.Posts.FindAsync(id);

        if (post == null)
        {
            return NotFound();
        }

        // Only allow updates to scheduled posts
        if (post.Status != PostStatus.Scheduled)
        {
            return BadRequest(new { error = "Cannot update a post that is not scheduled" });
        }

        var validationErrors = ValidateUpdatePostRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // For Facebook posts, verify the target page is connected and has a valid token
        if (request.Platform == Platform.Facebook)
        {
            if (!request.TargetPageId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "A Facebook Page must be selected to schedule a Facebook post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var targetPage = await _context.Set<ConnectedPage>()
                .FirstOrDefaultAsync(p => p.Id == request.TargetPageId.Value);

            if (targetPage == null)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration disconnected",
                    Detail = "The selected Facebook Page is no longer connected. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            if (string.IsNullOrEmpty(targetPage.AccessToken))
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration token missing",
                    Detail = "The selected Facebook Page's access token is missing. Please reconnect in Connected Accounts.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }
        }

        // Instagram-specific validation for updates
        if (request.Platform == Platform.Instagram)
        {
            if (!request.TargetInstagramAccountId.HasValue)
            {
                return Conflict(new ProblemDetails
                {
                    Title = "Integration required",
                    Detail = "An Instagram Business Account must be selected to schedule an Instagram post.",
                    Status = StatusCodes.Status409Conflict,
                    Extensions = { ["code"] = "INTEGRATION_DISCONNECTED" }
                });
            }

            var updateMediaType = request.MediaType ?? MediaType.None;
            if (string.IsNullOrEmpty(request.MediaUrl) || (updateMediaType != MediaType.Image && updateMediaType != MediaType.Video))
            {
                return BadRequest(new ProblemDetails
                {
                    Title = "Invalid media",
                    Detail = "Instagram Feed posts require exactly one media item (image or video).",
                    Status = StatusCodes.Status400BadRequest,
                });
            }
        }

        var scheduledAtChanged = post.ScheduledAt != request.ScheduledAt;

        post.Content = request.Content;
        post.MediaUrl = request.MediaUrl;
        post.MediaType = request.MediaType ?? MediaType.None;
        post.Platform = request.Platform;
        post.ScheduledAt = request.ScheduledAt;
        post.TargetPageId = request.TargetPageId;
        post.TargetInstagramAccountId = request.TargetInstagramAccountId;
        post.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // Reschedule if time changed
        if (scheduledAtChanged)
        {
            var scheduleResult = await _scheduler.RescheduleAsync(post);
            if (scheduleResult.Success && !string.IsNullOrEmpty(scheduleResult.ScheduleIdentifier))
            {
                post.ScheduleArn = scheduleResult.ScheduleIdentifier;
                await _context.SaveChangesAsync();
            }
        }

        return NoContent();
    }

    [HttpPost("{id}/publish-now")]
    public async Task<ActionResult<PostDto>> PublishNow(
        Guid id,
        [FromServices] IPostPublisherResolver publisherResolver,
        [FromServices] IStoryPublisherResolver storyPublisherResolver)
    {
        var post = await _context.Posts
            .Include(p => p.TargetPage)
            .Include(p => p.TargetInstagramAccount)
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null)
            return NotFound();

        // Only Scheduled posts can be published immediately
        if (post.Status != PostStatus.Scheduled)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Cannot publish now",
                Detail = $"Only scheduled posts can be published immediately. Current status: {post.Status}.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        _logger.LogInformation(
            "Publishing post {PostId} immediately (type={PostType}, platform={Platform})",
            post.Id, post.PostType, post.Platform);

        try
        {
            PublishResult result;

            if (post.PostType == PostType.Story)
            {
                var publisher = storyPublisherResolver.GetPublisher(post.Platform);
                if (publisher == null)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Unsupported platform",
                        Detail = $"Story publishing is not supported for {post.Platform}.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
                result = await publisher.PublishAsync(post.Id);
            }
            else
            {
                var publisher = publisherResolver.GetPublisher(post.Platform);
                if (publisher == null)
                {
                    return BadRequest(new ProblemDetails
                    {
                        Title = "Unsupported platform",
                        Detail = $"Publishing is not supported for {post.Platform}.",
                        Status = StatusCodes.Status400BadRequest,
                    });
                }
                result = await publisher.PublishAsync(post.Id);
            }

            // Reload to get fresh state after publishing
            await _context.Entry(post).ReloadAsync();
            await _context.Entry(post).Reference(p => p.TargetPage).LoadAsync();
            await _context.Entry(post).Reference(p => p.TargetInstagramAccount).LoadAsync();
            await _context.Entry(post).Collection(p => p.MediaItems).LoadAsync();

            if (result.Success)
            {
                return Ok(PostDto.FromEntity(post));
            }
            else
            {
                // Publishing failed — return the error but don't 500
                return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                {
                    Title = "Publishing failed",
                    Detail = result.ErrorMessage ?? "An error occurred while publishing to the platform.",
                    Status = StatusCodes.Status502BadGateway,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during publish-now for post {PostId}", post.Id);
            return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
            {
                Title = "Publishing failed",
                Detail = ex.Message,
                Status = StatusCodes.Status502BadGateway,
            });
        }
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> CancelPost(Guid id)
    {
        var post = await _context.Posts.FindAsync(id);

        if (post == null)
        {
            return NotFound();
        }

        switch (post.Status)
        {
            // Already canceled — idempotent
            case PostStatus.Canceled:
                return Ok();

            // Cannot cancel posts that are publishing or already published
            case PostStatus.Publishing:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot cancel post",
                    Detail = "This post is currently being published and cannot be canceled.",
                    Status = StatusCodes.Status409Conflict,
                });

            case PostStatus.Published:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot cancel post",
                    Detail = "This post has already been published and cannot be canceled.",
                    Status = StatusCodes.Status409Conflict,
                });

            // Failed — treat as idempotent (already stopped), mark Canceled
            case PostStatus.Failed:
                post.Status = PostStatus.Canceled;
                post.CanceledAt = DateTime.UtcNow;
                post.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return Ok();

            // Scheduled / RetryPending — cancel the schedule and mark as canceled
            default:
                await _scheduler.CancelScheduleAsync(post);

                post.Status = PostStatus.Canceled;
                post.CanceledAt = DateTime.UtcNow;
                post.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                return Ok();
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePost(Guid id)
    {
        var post = await _context.Posts
            .Include(p => p.MediaItems)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null)
        {
            return NotFound();
        }

        switch (post.Status)
        {
            // Only Canceled and Failed posts can be hard-deleted
            case PostStatus.Canceled:
            case PostStatus.Failed:
                _context.Posts.Remove(post);
                await _context.SaveChangesAsync();
                return NoContent();

            // Scheduled / RetryPending — must cancel first
            case PostStatus.Scheduled:
            case PostStatus.RetryPending:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot delete post",
                    Detail = "Cancel the scheduled post before deleting.",
                    Status = StatusCodes.Status409Conflict,
                });

            // Publishing / Published — cannot delete
            case PostStatus.Publishing:
            case PostStatus.Published:
            default:
                return Conflict(new ProblemDetails
                {
                    Title = "Cannot delete post",
                    Detail = "Cannot delete a post that is publishing or already published.",
                    Status = StatusCodes.Status409Conflict,
                });
        }
    }

    private static Dictionary<string, string[]> ValidateCreatePostRequest(CreatePostRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        // Content length validation (stories allow empty content)
        if (!string.IsNullOrEmpty(request.Content))
        {
            var maxChars = ValidationLimits.GetPostTextMaxChars(request.Platform);
            if (request.Content.Length > maxChars)
            {
                errors["content"] = [$"Text is too long for {request.Platform}. Max {maxChars} characters."];
            }
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateUpdatePostRequest(UpdatePostRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        var maxChars = ValidationLimits.GetPostTextMaxChars(request.Platform);
        if (request.Content?.Length > maxChars)
        {
            errors["content"] = [$"Text is too long for {request.Platform}. Max {maxChars} characters."];
        }

        return errors;
    }
}

public record CreatePostMediaItem(
    string MediaUrl,
    MediaType MediaType,
    int Order
);

public record CreatePostRequest(
    string? Content,
    string? MediaUrl,
    MediaType? MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    PostType PostType = PostType.Feed,
    Guid? TargetPageId = null,
    Guid? TargetInstagramAccountId = null,
    string? SelectedThumbnailUrl = null,
    List<Guid>? MediaAssetIds = null,
    List<CreatePostMediaItem>? MediaItems = null
);

public record UpdatePostRequest(
    string Content,
    string? MediaUrl,
    MediaType? MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    Guid? TargetPageId = null,
    Guid? TargetInstagramAccountId = null
);

public record PaginatedResponse<T>(
    List<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages
)
{
    public bool HasNextPage => Page < TotalPages;
    public bool HasPreviousPage => Page > 1;
}

public record PostMediaItemDto(
    Guid Id,
    int Order,
    string MediaUrl,
    MediaType MediaType
);

public record PostDto(
    Guid Id,
    string Content,
    string? MediaUrl,
    MediaType MediaType,
    PostType PostType,
    Platform Platform,
    DateTime ScheduledAt,
    PostStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TargetPageId,
    string? TargetPageName,
    Guid? TargetInstagramAccountId,
    string? TargetInstagramAccountName,
    DateTime? PublishedAt,
    string? ExternalPostId,
    string? ExternalPostUrl,
    string? ErrorMessage,
    int RetryCount,
    string? SelectedThumbnailUrl,
    string? InstagramMediaType,
    List<PostMediaItemDto>? MediaItems = null
)
{
    public static PostDto FromEntity(Post post) => new(
        post.Id,
        post.Content,
        post.MediaUrl,
        post.MediaType,
        post.PostType,
        post.Platform,
        post.ScheduledAt,
        post.Status,
        post.CreatedAt,
        post.UpdatedAt,
        post.TargetPageId,
        post.TargetPage?.Name,
        post.TargetInstagramAccountId,
        post.TargetInstagramAccount != null
            ? $"@{post.TargetInstagramAccount.Username}"
            : null,
        post.PublishedAt,
        post.ExternalPostId,
        post.ExternalPostUrl,
        post.ErrorMessage,
        post.RetryCount,
        post.SelectedThumbnailUrl,
        post.InstagramMediaType?.ToString(),
        post.MediaItems?.Count > 0
            ? post.MediaItems.OrderBy(m => m.Order).Select(m => new PostMediaItemDto(m.Id, m.Order, m.MediaUrl, m.MediaType)).ToList()
            : null
    );
}
