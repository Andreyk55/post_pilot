namespace PostPilot.Api.Services.Auth;

/// <summary>
/// Reads the currently-authenticated user id from the request's ClaimsPrincipal.
/// Throws <see cref="UnauthorizedAccessException"/> when the cookie is missing
/// or malformed — controllers should be marked [Authorize] so this is rare.
/// </summary>
public interface ICurrentUserProvider
{
    Guid GetCurrentUserId();
    bool TryGetCurrentUserId(out Guid userId);
}
