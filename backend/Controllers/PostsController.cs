using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PostsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPostScheduler _scheduler;
    private readonly ILogger<PostsController> _logger;

    public PostsController(
        AppDbContext context,
        IPostScheduler scheduler,
        ILogger<PostsController> logger)
    {
        _context = context;
        _scheduler = scheduler;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PostDto>>> GetPosts()
    {
        var posts = await _context.Posts
            .Include(p => p.TargetPage)
            .OrderByDescending(p => p.ScheduledAt)
            .Select(p => PostDto.FromEntity(p))
            .ToListAsync();

        return posts;
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

    [HttpPost]
    public async Task<ActionResult<PostDto>> CreatePost(CreatePostRequest request)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = request.Content,
            MediaUrl = request.MediaUrl,
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
    Platform Platform,
    DateTime ScheduledAt,
    Guid? TargetPageId = null
);

public record UpdatePostRequest(
    string Content,
    string? MediaUrl,
    Platform Platform,
    DateTime ScheduledAt,
    Guid? TargetPageId = null
);

public record PostDto(
    Guid Id,
    string Content,
    string? MediaUrl,
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
