using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Support;

namespace PostPilot.Api.Controllers;

/// <summary>
/// Authenticated in-app "Contact Us" support form. The sender is ALWAYS the authenticated
/// principal — any userId/accountId/email in the request body is ignored (the bound DTO
/// has no such fields). There is no public contact form and no support email is exposed.
/// </summary>
[ApiController]
[Authorize]
[Route("api/support")]
public sealed class SupportController : ControllerBase
{
    private readonly ISupportContactService _support;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;

    public SupportController(
        ISupportContactService support,
        ICurrentUserProvider currentUser,
        ICurrentWorkspaceProvider currentWorkspace)
    {
        _support = support;
        _currentUser = currentUser;
        _currentWorkspace = currentWorkspace;
    }

    [HttpPost("contact")]
    [ProducesResponseType(typeof(SupportContactResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Contact(
        [FromBody] CreateSupportContactRequest request,
        CancellationToken ct)
    {
        // Sender is ALWAYS the authenticated principal — never the request body.
        var userId = _currentUser.GetCurrentUserId();

        // Workspace is best-effort context only. Contact Us must work even when no
        // workspace is selected (e.g. an account issue blocks workspace use), so we
        // swallow the strict resolver's failures and store null rather than letting
        // them surface as 409/403 via WorkspaceResolutionExceptionMiddleware.
        var workspaceId = await TryResolveWorkspaceIdAsync(ct);

        try
        {
            var response = await _support.CreateAsync(userId, workspaceId, request, ct);
            return StatusCode(StatusCodes.Status201Created, response);
        }
        catch (SupportValidationException ex)
        {
            return ValidationProblem(new ValidationProblemDetails(
                ex.Errors.ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
        catch (SupportRateLimitExceededException ex)
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }

    private async Task<Guid?> TryResolveWorkspaceIdAsync(CancellationToken ct)
    {
        try
        {
            return await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
        }
        catch (WorkspaceNotSelectedException)
        {
            // No usable selected workspace — fine, WorkspaceId is nullable.
            return null;
        }
        catch (WorkspaceAccessDeniedException)
        {
            // Selected workspace exists but the user lost access — don't attribute the
            // message to a workspace they can't use.
            return null;
        }
    }
}
