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

        // Ensure the user has at least one workspace where they are Owner.
        // The "default" workspace is the oldest one they own — stable across
        // logins so the user doesn't keep hopping between workspaces.
        var workspace = await _db.Workspaces
            .Where(w => w.OwnerUserId == user.Id)
            .OrderBy(w => w.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (workspace is null)
        {
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
            // Owner row should already exist from initial provisioning, but
            // self-heal in case a previous failure left a workspace without
            // the membership row.
            var ownerMembership = await _db.WorkspaceMembers
                .FirstOrDefaultAsync(
                    m => m.WorkspaceId == workspace.Id && m.UserId == user.Id, ct);
            if (ownerMembership is null)
            {
                _db.WorkspaceMembers.Add(new WorkspaceMember
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = workspace.Id,
                    UserId = user.Id,
                    Role = WorkspaceRole.Owner,
                    CreatedAt = now,
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        return new ProvisionedUser(user, workspace.Id, workspace.Name, isNew);
    }

    private static string DefaultWorkspaceName(string displayName)
    {
        var trimmed = displayName?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "My Workspace";
        return $"{trimmed}'s Workspace";
    }
}
