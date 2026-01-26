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
        [FromQuery] PostStatus? status = null)
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

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        var posts = await query
            .Include(p => p.TargetPage)
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
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (post == null)
        {
            return NotFound();
        }

        // Fetch engagement metrics for published Facebook posts
        PostEngagementDto? engagement = null;
        string? externalPostUrl = null;

        if (post.Platform == Platform.Facebook &&
            post.Status == PostStatus.Published &&
            !string.IsNullOrEmpty(post.ExternalPostId))
        {
            // Build external URL to the Facebook post
            externalPostUrl = $"https://www.facebook.com/{post.ExternalPostId}";

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
            Platform: post.Platform.ToString(),
            ScheduledAt: post.ScheduledAt,
            Status: post.Status.ToString(),
            CreatedAt: post.CreatedAt,
            UpdatedAt: post.UpdatedAt,
            TargetPageId: post.TargetPageId,
            TargetPageName: post.TargetPage?.Name,
            PublishedAt: post.PublishedAt,
            ExternalPostId: post.ExternalPostId,
            ErrorMessage: post.ErrorMessage,
            RetryCount: post.RetryCount,
            Engagement: engagement,
            ExternalPostUrl: externalPostUrl
        );
    }

    [HttpPost]
    public async Task<ActionResult<PostDto>> CreatePost(CreatePostRequest request)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = request.Content,
            MediaUrl = request.MediaUrl,
            MediaType = request.MediaType ?? MediaType.None,
            Platform = request.Platform,
            ScheduledAt = request.ScheduledAt,
            TargetPageId = request.TargetPageId,
            Status = PostStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

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

        // Reload with TargetPage for the response
        await _context.Entry(post).Reference(p => p.TargetPage).LoadAsync();

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

        // Only allow updates to pending posts
        if (post.Status != PostStatus.Pending)
        {
            return BadRequest(new { error = "Cannot update a post that is not pending" });
        }

        var scheduledAtChanged = post.ScheduledAt != request.ScheduledAt;

        post.Content = request.Content;
        post.MediaUrl = request.MediaUrl;
        post.MediaType = request.MediaType ?? MediaType.None;
        post.Platform = request.Platform;
        post.ScheduledAt = request.ScheduledAt;
        post.TargetPageId = request.TargetPageId;
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

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePost(Guid id)
    {
        var post = await _context.Posts.FindAsync(id);

        if (post == null)
        {
            return NotFound();
        }

        // Cancel any scheduled trigger before deleting
        await _scheduler.CancelScheduleAsync(post);

        _context.Posts.Remove(post);
        await _context.SaveChangesAsync();

        return NoContent();
    }
}

public record CreatePostRequest(
    string Content,
    string? MediaUrl,
    MediaType? MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    Guid? TargetPageId = null
);

public record UpdatePostRequest(
    string Content,
    string? MediaUrl,
    MediaType? MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    Guid? TargetPageId = null
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

public record PostDto(
    Guid Id,
    string Content,
    string? MediaUrl,
    MediaType MediaType,
    Platform Platform,
    DateTime ScheduledAt,
    PostStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? TargetPageId,
    string? TargetPageName,
    DateTime? PublishedAt,
    string? ExternalPostId,
    string? ErrorMessage,
    int RetryCount
)
{
    public static PostDto FromEntity(Post post) => new(
        post.Id,
        post.Content,
        post.MediaUrl,
        post.MediaType,
        post.Platform,
        post.ScheduledAt,
        post.Status,
        post.CreatedAt,
        post.UpdatedAt,
        post.TargetPageId,
        post.TargetPage?.Name,
        post.PublishedAt,
        post.ExternalPostId,
        post.ErrorMessage,
        post.RetryCount
    );
}
