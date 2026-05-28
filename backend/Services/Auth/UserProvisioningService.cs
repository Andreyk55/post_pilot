using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Auth;

public class UserProvisioningService : IUserProvisioningService
{
    private readonly AppDbContext _db;
    private readonly ILogger<UserProvisioningService> _logger;

    public UserProvisioningService(AppDbContext db, ILogger<UserProvisioningService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ProvisionedUser> ProvisionAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var user = await _db.AppUsers
            .FirstOrDefaultAsync(
                u => u.AuthProvider == identity.Provider
                  && u.ExternalAuthUserId == identity.ExternalUserId,
                ct);

        var isNew = user is null;
        if (user is null)
        {
            user = new AppUser
            {
                Id = Guid.NewGuid(),
                Email = identity.Email,
                DisplayName = identity.DisplayName,
                AuthProvider = identity.Provider,
                ExternalAuthUserId = identity.ExternalUserId,
                AvatarUrl = identity.AvatarUrl,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.AppUsers.Add(user);
            _logger.LogInformation(
                "Provisioning new AppUser {UserId} via {Provider}",
                user.Id, identity.Provider);
        }
        else
        {
            // Refresh mutable profile fields from the provider on every login.
            // Provider is the source of truth for display name + avatar.
            var changed = false;
            if (user.Email != identity.Email) { user.Email = identity.Email; changed = true; }
            if (user.DisplayName != identity.DisplayName) { user.DisplayName = identity.DisplayName; changed = true; }
            if (user.AvatarUrl != identity.AvatarUrl) { user.AvatarUrl = identity.AvatarUrl; changed = true; }
            if (changed) user.UpdatedAt = now;
        }

        // Find user's existing workspaces via membership (authoritative table).
        var membership = await (
            from m in _db.WorkspaceMembers
            join w in _db.Workspaces on m.WorkspaceId equals w.Id
            where m.UserId == user.Id
            orderby m.CreatedAt
            select new { Workspace = w, Member = m }
        ).FirstOrDefaultAsync(ct);

        Workspace workspace;
        if (membership is null)
        {
            // No workspace yet — create a default one and make the user Owner.
            workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                Name = DefaultWorkspaceName(identity.DisplayName),
                OwnerUserId = user.Id,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Workspaces.Add(workspace);

            _db.WorkspaceMembers.Add(new WorkspaceMember
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspace.Id,
                UserId = user.Id,
                Role = WorkspaceRole.Owner,
                CreatedAt = now,
            });
            _logger.LogInformation(
                "Created default workspace {WorkspaceId} for AppUser {UserId}",
                workspace.Id, user.Id);
        }
        else
        {
            workspace = membership.Workspace;
        }

        // Ensure CurrentWorkspaceId is set and points at a workspace where the user is a member.
        // If it's null or stale, repair it to the first valid membership.
        if (!user.CurrentWorkspaceId.HasValue)
        {
            user.CurrentWorkspaceId = workspace.Id;
            user.UpdatedAt = now;
        }
        else
        {
            var stillMember = await _db.WorkspaceMembers
                .AnyAsync(m => m.UserId == user.Id && m.WorkspaceId == user.CurrentWorkspaceId.Value, ct);
            if (!stillMember)
            {
                user.CurrentWorkspaceId = workspace.Id;
                user.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);

        // Re-read the active workspace name for the return value
        // (CurrentWorkspaceId may differ from the just-created workspace).
        var currentWorkspaceId = user.CurrentWorkspaceId!.Value;
        var currentWorkspaceName = currentWorkspaceId == workspace.Id
            ? workspace.Name
            : await _db.Workspaces.Where(w => w.Id == currentWorkspaceId).Select(w => w.Name).FirstAsync(ct);

        return new ProvisionedUser(user, currentWorkspaceId, currentWorkspaceName, isNew);
    }

    private static string DefaultWorkspaceName(string displayName)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "My Workspace";
        return $"{trimmed}'s Workspace";
    }
}
