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

        // No workspace selected. We deliberately do NOT pick one for the user: in the
        // MVP each workspace can have its own connected provider account, so silently
        // choosing a workspace could publish/upload into the wrong account. The client
        // must select a workspace explicitly.
        if (!user.CurrentWorkspaceId.HasValue)
        {
            _logger.LogWarning("User {UserId} has no CurrentWorkspaceId selected.", userId);
            throw new WorkspaceNotSelectedException(
                "No workspace is currently selected. Select a workspace and retry.");
        }

        var workspaceId = user.CurrentWorkspaceId.Value;

        // Does the selected workspace still exist?
        var workspace = await _db.Workspaces
            .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace is null)
        {
            // Stale: the workspace was deleted out from under the selection. Force the
            // client to re-select rather than guessing a replacement.
            _logger.LogWarning(
                "User {UserId} has stale CurrentWorkspaceId={Stale} (workspace no longer exists).",
                userId, workspaceId);
            throw new WorkspaceNotSelectedException(
                "The selected workspace no longer exists. Select a workspace and retry.");
        }

        // Is the user actually a member of the selected workspace? Always re-checked in
        // the DB — never trusted from a claim or a frontend-supplied id.
        var isMember = await _db.WorkspaceMembers
            .AnyAsync(m => m.UserId == userId && m.WorkspaceId == workspaceId, ct);
        if (!isMember)
        {
            _logger.LogWarning(
                "User {UserId} is not a member of selected workspace {WorkspaceId}.",
                userId, workspaceId);
            throw new WorkspaceAccessDeniedException(
                "You do not have access to the selected workspace.");
        }

        _cached = new CurrentWorkspaceInfo(userId, workspace.Id, workspace.Name);
        return _cached;
    }
}
