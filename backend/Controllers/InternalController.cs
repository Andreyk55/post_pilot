using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.Data;
using PostPilot.Api.Services.Publishing;

namespace PostPilot.Api.Controllers;

/// <summary>
/// Internal endpoints for scheduler-triggered operations.
/// In production, these are called by EventBridge via Lambda.
/// In local dev, they're called by the background service.
/// </summary>
[ApiController]
[Route("api/internal")]
public class InternalController : ControllerBase
{
    private readonly IPostPublisherResolver _publisherResolver;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<InternalController> _logger;

    public InternalController(
        IPostPublisherResolver publisherResolver,
        AppDbContext dbContext,
        ILogger<InternalController> logger)
    {
        _publisherResolver = publisherResolver;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Trigger publication of a scheduled post.
    /// Called by EventBridge scheduler or local background service.
    /// </summary>
    /// <param name="postId">The ID of the post to publish</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpPost("publish/{postId:guid}")]
    public async Task<IActionResult> PublishPost(Guid postId, CancellationToken cancellationToken)
    {
        // TODO: Add authentication for production (API key or IAM)
        // For now, this endpoint is accessible without auth

        _logger.LogInformation("Received publish request for post {PostId}", postId);

        var post = await _dbContext.Posts.FindAsync(new object[] { postId }, cancellationToken);
        if (post == null)
        {
            _logger.LogWarning("Post {PostId} not found", postId);
            return NotFound(new { error = "Post not found" });
        }

        var publisher = _publisherResolver.GetPublisher(post.Platform);
        if (publisher == null)
        {
            _logger.LogWarning("No publisher available for platform {Platform}", post.Platform);
            return BadRequest(new { error = $"No publisher available for platform {post.Platform}" });
        }

        var result = await publisher.PublishAsync(postId, cancellationToken);

        if (result.Success)
        {
            return Ok(new
            {
                success = true,
                externalPostId = result.ExternalPostId,
                alreadyPublished = result.ErrorType == PublishErrorType.AlreadyPublished
            });
        }

        // Return appropriate status based on error type
        return result.ErrorType switch
        {
            PublishErrorType.Permanent => BadRequest(new
            {
                success = false,
                error = result.ErrorMessage,
                willRetry = false
            }),
            PublishErrorType.AlreadyPublished => Ok(new
            {
                success = true,
                alreadyPublished = true,
                message = result.ErrorMessage
            }),
            _ => StatusCode(503, new
            {
                success = false,
                error = result.ErrorMessage,
                willRetry = true
            })
        };
    }

    /// <summary>
    /// Health check endpoint for internal services
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
