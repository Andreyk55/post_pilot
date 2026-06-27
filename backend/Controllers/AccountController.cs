using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Account;
using PostPilot.Api.Services.Auth;

namespace PostPilot.Api.Controllers;

/// <summary>
/// Authenticated full-account deletion. The target user is ALWAYS the authenticated
/// principal — any userId/accountId in the request body is ignored. Requires the user
/// to type the exact confirmation phrase.
/// </summary>
[ApiController]
[Authorize]
[Route("api/account")]
public sealed class AccountController : ControllerBase
{
    /// <summary>Exact phrase the user must type to confirm irreversible deletion.</summary>
    public const string ConfirmationPhrase = "DELETE MY ACCOUNT";

    private readonly IAccountDeletionService _accountDeletion;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IAccountDeletionService accountDeletion,
        ICurrentUserProvider currentUser,
        ILogger<AccountController> logger)
    {
        _accountDeletion = accountDeletion;
        _currentUser = currentUser;
        _logger = logger;
    }

    [HttpDelete]
    public Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest? request, CancellationToken ct) =>
        DeleteCurrentAccountAsync(request, ct);

    // POST alias for clients/proxies that cannot send a DELETE body.
    [HttpPost("delete")]
    public Task<IActionResult> DeleteAccountViaPost([FromBody] DeleteAccountRequest? request, CancellationToken ct) =>
        DeleteCurrentAccountAsync(request, ct);

    private async Task<IActionResult> DeleteCurrentAccountAsync(DeleteAccountRequest? request, CancellationToken ct)
    {
        // Target is ALWAYS the authenticated principal — request body ids are never read.
        var userId = _currentUser.GetCurrentUserId();

        var typed = request?.ConfirmationText?.Trim();
        if (!string.Equals(typed, ConfirmationPhrase, StringComparison.Ordinal))
        {
            return BadRequest(new { error = "confirmation_text_mismatch" });
        }

        await _accountDeletion.DeleteCurrentAccountAsync(userId, ct);

        // Clear the session so the now-orphaned cookie can't be reused.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        _logger.LogInformation("Account deleted for user {UserId}.", userId);
        return Ok(new { success = true });
    }
}
