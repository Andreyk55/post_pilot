using PostPilot.Api.Services.PrivateAccess;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Middleware;

/// <summary>
/// Blocks /api/* requests when the private-access gate is enabled and the
/// caller does not present a valid signed cookie. Allow-listed paths:
///   /api/private-access/login
///   /api/private-access/me
///   /api/private-access/logout
///   /health
/// CORS preflight (OPTIONS) is always allowed through so the browser can
/// negotiate cross-origin requests before sending the cookie.
/// </summary>
public class PrivateAccessMiddleware
{
    private static readonly string[] AllowedPaths =
    {
        "/api/private-access/login",
        "/api/private-access/me",
        "/api/private-access/logout",
        "/health",
    };

    private readonly RequestDelegate _next;
    private readonly PrivateAccessOptions _options;
    private readonly IPrivateAccessTokenService _tokenService;

    public PrivateAccessMiddleware(
        RequestDelegate next,
        PrivateAccessOptions options,
        IPrivateAccessTokenService tokenService)
    {
        _next = next;
        _options = options;
        _tokenService = tokenService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Never gate CORS preflight — the browser sends it without cookies.
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;

        if (IsAllowedPath(path))
        {
            await _next(context);
            return;
        }

        // Only gate API surface. Static assets and the root catch-all (if any)
        // are left alone — the frontend is hosted elsewhere anyway.
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var token = context.Request.Cookies[_options.CookieName];
        if (!string.IsNullOrEmpty(token) && _tokenService.ValidateToken(token))
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"private_access_required\"}");
    }

    private static bool IsAllowedPath(string path)
    {
        foreach (var allowed in AllowedPaths)
        {
            if (string.Equals(path, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
