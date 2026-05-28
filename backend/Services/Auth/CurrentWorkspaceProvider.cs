using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;

namespace PostPilot.Api.Services.Auth;

public class CurrentWorkspaceProvider : ICurrentWorkspaceProvider
{
    private readonly AppDbContext _db;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ILogger<CurrentWorkspaceProvider> _logger;

    // Per-request memoization. Scoped lifetime means a single instance per HTTP request.
    private CurrentWorkspaceInfo? _cached;

    public CurrentWorkspaceProvider(
        AppDbContext db,
        ICurrentUserProvider currentUser,
        ILogger<CurrentWorkspaceProvider> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken ct = default)
    {
        var info = await GetCurrentWorkspaceAsync(ct);
        return info.WorkspaceId;
    }

    public async Task<CurrentWorkspaceInfo> GetCurrentWorkspaceAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;

        var userId = _currentUser.GetCurrentUserId();
        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");

        // If CurrentWorkspaceId is set, verify it points to a workspace the user is a member of.
        if (user.CurrentWorkspaceId.HasValue)
        {
            var verified = await (
                from m in _db.WorkspaceMembers
                join w in _db.Workspaces on m.WorkspaceId equals w.Id
                where m.UserId == userId && m.WorkspaceId == user.CurrentWorkspaceId.Value
                select new { w.Id, w.Name }
            ).FirstOrDefaultAsync(ct);

            if (verified is not null)
            {
                _cached = new CurrentWorkspaceInfo(userId, verified.Id, verified.Name);
                return _cached;
            }

            _logger.LogWarning(
                "User {UserId} had stale CurrentWorkspaceId={Stale}; repairing.",
                userId, user.CurrentWorkspaceId);
        }

        // Self-heal: pick the user's oldest membership.
        var fallback = await (
            from m in _db.WorkspaceMembers
            join w in _db.Workspaces on m.WorkspaceId equals w.Id
            where m.UserId == userId
            orderby m.CreatedAt
            select new { w.Id, w.Name }
        ).FirstOrDefaultAsync(ct);

        if (fallback is null)
        {
            throw new UnauthorizedAccessException("User has no workspace memberships.");
        }

        user.CurrentWorkspaceId = fallback.Id;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _cached = new CurrentWorkspaceInfo(userId, fallback.Id, fallback.Name);
        return _cached;
    }
}
