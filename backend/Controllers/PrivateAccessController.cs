using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.Services.PrivateAccess;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Controllers;

/// <summary>
/// Temporary single-password gate. Removed once real auth lands.
///
/// Endpoints:
///   POST /api/private-access/login   — validate password, set cookie
///   GET  /api/private-access/me      — report whether the caller has access
///   POST /api/private-access/logout  — clear the cookie
///
/// All three endpoints are explicitly allow-listed in
/// <see cref="Middleware.PrivateAccessMiddleware"/> so they remain reachable
/// even when the gate is enabled.
/// </summary>
[ApiController]
[Route("api/private-access")]
public class PrivateAccessController : ControllerBase
{
    private readonly PrivateAccessOptions _options;
    private readonly IPrivateAccessTokenService _tokenService;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PrivateAccessController> _logger;

    public PrivateAccessController(
        PrivateAccessOptions options,
        IPrivateAccessTokenService tokenService,
        IWebHostEnvironment env,
        ILogger<PrivateAccessController> logger)
    {
        _options = options;
        _tokenService = tokenService;
        _env = env;
        _logger = logger;
    }

    public class LoginRequest
    {
        public string Password { get; set; } = string.Empty;
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        if (!_options.Enabled)
        {
            // Gate is off — treat any login attempt as a no-op success so
            // the frontend works in dev without configuration.
            return Ok(new { hasAccess = true });
        }

        if (string.IsNullOrEmpty(_options.PasswordHash))
        {
            _logger.LogError("PrivateAccess.Enabled=true but PasswordHash is empty — login impossible.");
            return StatusCode(500, new { error = "private_access_not_configured" });
        }

        if (request is null || string.IsNullOrEmpty(request.Password))
        {
            return Unauthorized(new { error = "invalid_password" });
        }

        bool ok;
        try
        {
            ok = BCrypt.Net.BCrypt.Verify(request.Password, _options.PasswordHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            _logger.LogError("PrivateAccess.PasswordHash is not a valid BCrypt hash.");
            return StatusCode(500, new { error = "private_access_not_configured" });
        }

        if (!ok)
        {
            _logger.LogWarning("Private-access login rejected from {Ip}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new { error = "invalid_password" });
        }

        var expiresAt = DateTimeOffset.UtcNow.AddDays(Math.Max(1, _options.SessionDays));
        var token = _tokenService.IssueToken(expiresAt);

        Response.Cookies.Append(_options.CookieName, token, BuildCookieOptions(expiresAt));
        return Ok(new { hasAccess = true });
    }

    [HttpGet("me")]
    public IActionResult Me()
    {
        if (!_options.Enabled)
        {
            return Ok(new { hasAccess = true });
        }

        var token = Request.Cookies[_options.CookieName];
        var hasAccess = !string.IsNullOrEmpty(token) && _tokenService.ValidateToken(token);
        return Ok(new { hasAccess });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        // Delete with matching attributes so the browser actually drops it.
        Response.Cookies.Delete(_options.CookieName, BuildCookieOptions(DateTimeOffset.UtcNow));
        return Ok(new { success = true });
    }

    private CookieOptions BuildCookieOptions(DateTimeOffset expiresAt)
    {
        var isProduction = _env.IsProduction();
        return new CookieOptions
        {
            HttpOnly = true,
            // Cross-site cookie (Vercel frontend → VPS backend) requires
            // SameSite=None, which in turn requires Secure=true. In dev on
            // http://localhost we keep SameSite=Lax + Secure=false so the
            // cookie actually sticks.
            Secure = isProduction,
            SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax,
            Expires = expiresAt,
            Path = "/",
            IsEssential = true,
        };
    }
}
