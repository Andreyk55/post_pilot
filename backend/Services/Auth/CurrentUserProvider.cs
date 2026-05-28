using System.Security.Claims;

namespace PostPilot.Api.Services.Auth;

public class CurrentUserProvider : ICurrentUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid GetCurrentUserId()
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            throw new UnauthorizedAccessException("No authenticated user on the current request.");
        }
        return userId;
    }

    public bool TryGetCurrentUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true) return false;

        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
