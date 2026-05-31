using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api;
using PostPilot.Api.Data;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Controllers;

/// <summary>
/// Real-user authentication endpoints (Google OAuth + cookie session).
/// Independent of the global password gate — the gate runs first in
/// middleware and only after the user has the private-access cookie can
/// they reach these endpoints.
///
///   GET  /api/auth/google/start     — kicks off Google OAuth
///   GET  /api/auth/google/callback  — Google redirects here after consent
///   GET  /api/auth/me               — current logged-in user (401 if none)
///   POST /api/auth/logout           — clears session cookie
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    public const string ProviderGoogle = "google";

    private readonly AppDbContext _db;
    private readonly AuthOptions _authOptions;
    private readonly IUserProvisioningService _provisioning;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        AppDbContext db,
        AuthOptions authOptions,
        IUserProvisioningService provisioning,
        ICurrentWorkspaceProvider currentWorkspace,
        ILogger<AuthController> logger)
    {
        _db = db;
        _authOptions = authOptions;
        _provisioning = provisioning;
        _currentWorkspace = currentWorkspace;
        _logger = logger;
    }

    [HttpGet("google/start")]
    public IActionResult GoogleStart([FromQuery] string? returnUrl)
    {
        var safeReturn = SanitizeReturnUrl(returnUrl);

        var props = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(GoogleCallback), "Auth"),
        };
        props.Items["postpilot:return_url"] = safeReturn;

        return Challenge(props, GoogleDefaults.AuthenticationScheme);
    }

    [HttpGet("google/callback")]
    public async Task<IActionResult> GoogleCallback()
    {
        var result = await HttpContext.AuthenticateAsync(Startup.ExternalAuthScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            _logger.LogWarning("Google callback authentication failed.");
            return RedirectToFrontendCallback(error: "google_auth_failed");
        }

        var externalId = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email = result.Principal.FindFirstValue(ClaimTypes.Email);
        var name = result.Principal.FindFirstValue(ClaimTypes.Name)
                ?? result.Principal.FindFirstValue(ClaimTypes.GivenName)
                ?? email;
        var avatar = result.Principal.FindFirstValue("urn:google:picture")
                  ?? result.Principal.FindFirstValue("picture");

        if (string.IsNullOrEmpty(externalId) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name))
        {
            _logger.LogWarning(
                "Google callback missing required claims (sub/email/name).");
            return RedirectToFrontendCallback(error: "google_claims_missing");
        }

        var provisioned = await _provisioning.ProvisionAsync(new ExternalIdentity(
            Provider: ProviderGoogle,
            ExternalUserId: externalId,
            Email: email,
            DisplayName: name,
            AvatarUrl: avatar));

        // Cookie claims identify only the user. Current workspace is loaded from
        // AppUser.CurrentWorkspaceId in the DB so the frontend can switch workspaces
        // without re-issuing the cookie.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, provisioned.User.Id.ToString()),
            new(ClaimTypes.Email, provisioned.User.Email),
            new(ClaimTypes.Name, provisioned.User.DisplayName),
        };
        if (!string.IsNullOrEmpty(provisioned.User.AvatarUrl))
        {
            claims.Add(new Claim("urn:postpilot:avatar", provisioned.User.AvatarUrl));
        }
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });

        await HttpContext.SignOutAsync(Startup.ExternalAuthScheme);

        var returnUrl = result.Properties?.Items.TryGetValue("postpilot:return_url", out var ru) == true
            ? ru
            : null;
        return RedirectToFrontendCallback(returnUrl: returnUrl);
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdStr, out var userId))
        {
            return Unauthorized();
        }

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            // Cookie outlived the user row (manual DB cleanup, etc.) — force re-login.
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Unauthorized();
        }

        // /me intentionally tolerates an unresolved workspace: it's the endpoint the
        // frontend uses to discover whether a workspace is selected. We report the user
        // with a null currentWorkspaceId rather than failing the whole call or — as the
        // old code did — silently auto-selecting one. Workspace-scoped endpoints still
        // hard-fail (409/403) via the resolver; only this discovery endpoint is lenient.
        CurrentWorkspaceInfo? info = null;
        try
        {
            info = await _currentWorkspace.GetCurrentWorkspaceAsync();
        }
        catch (UnauthorizedAccessException)
        {
            // No authenticated user / user row gone — force re-login.
            return Unauthorized();
        }
        catch (WorkspaceNotSelectedException)
        {
            // No valid selected workspace yet; surface null so the client can prompt.
        }
        catch (WorkspaceAccessDeniedException)
        {
            // Selected workspace exists but the user lost access; surface null so the
            // client can re-select rather than operating in the wrong account.
        }

        return Ok(new
        {
            id = user.Id,
            email = user.Email,
            displayName = user.DisplayName,
            avatarUrl = user.AvatarUrl,
            currentWorkspaceId = info?.WorkspaceId,
            workspaceName = info?.WorkspaceName,
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        // Minimal CSRF protection on this cookie-authenticated POST: require
        // the Origin header to match a configured frontend origin.
        if (!IsTrustedOrigin(Request.Headers.Origin.ToString()))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "untrusted_origin" });
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Ok(new { success = true });
    }

    private IActionResult RedirectToFrontendCallback(string? returnUrl = null, string? error = null)
    {
        var baseUrl = _authOptions.FrontendUrl?.TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            baseUrl = $"{Request.Scheme}://{Request.Host}";
        }

        var target = $"{baseUrl}/auth/callback";
        var queryParts = new List<string>();
        if (!string.IsNullOrEmpty(error))
        {
            queryParts.Add($"error={Uri.EscapeDataString(error)}");
        }
        if (!string.IsNullOrEmpty(returnUrl))
        {
            queryParts.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
        }
        if (queryParts.Count > 0)
        {
            target += "?" + string.Join("&", queryParts);
        }
        return Redirect(target);
    }

    private static string SanitizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return "/";
        if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)) return "/";
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//")) return "/";
        return returnUrl;
    }

    private bool IsTrustedOrigin(string origin)
    {
        if (string.IsNullOrEmpty(origin)) return false;
        if (origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase)) return true;
        if (_authOptions.AllowedOrigins is { Length: > 0 } allowed
            && Array.IndexOf(allowed, origin) >= 0) return true;
        if (!string.IsNullOrEmpty(_authOptions.FrontendUrl)
            && string.Equals(origin.TrimEnd('/'), _authOptions.FrontendUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }
}
