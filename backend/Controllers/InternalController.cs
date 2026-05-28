using Microsoft.AspNetCore.Mvc;

namespace PostPilot.Api.Controllers;

/// <summary>
/// Internal endpoints. Originally hosted a publish trigger that an external scheduler
/// (e.g. EventBridge → Lambda) was meant to call. That design was abandoned: the
/// in-process <see cref="Services.Scheduling.PostPublishingWorker"/> drives publishing
/// directly. The publish endpoint was unauthenticated, took only a post id, and did
/// no workspace check — so it was removed.
///
/// Only the cheap health probe remains; it is referenced by docs/deployment-vps.md.
/// </summary>
[ApiController]
[Route("api/internal")]
public class InternalController : ControllerBase
{
    /// <summary>
    /// Health check endpoint for internal services.
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
