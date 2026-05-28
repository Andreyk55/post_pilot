using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;

namespace PostPilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/workspaces")]
public class WorkspacesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ILogger<WorkspacesController> _logger;

    public WorkspacesController(
        AppDbContext db,
        ICurrentUserProvider currentUser,
        ILogger<WorkspacesController> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// Lists workspaces the current user is a member of. Other workspaces are
    /// never disclosed.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var userId = _currentUser.GetCurrentUserId();
        var currentWorkspaceId = await _db.AppUsers
            .Where(u => u.Id == userId)
            .Select(u => u.CurrentWorkspaceId)
            .FirstOrDefaultAsync(ct);

        var items = await (
            from m in _db.WorkspaceMembers
            join w in _db.Workspaces on m.WorkspaceId equals w.Id
            where m.UserId == userId
            orderby w.CreatedAt
            select new
            {
                id = w.Id,
                name = w.Name,
                role = m.Role.ToString(),
                isCurrent = currentWorkspaceId == w.Id,
            }
        ).ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>
    /// Creates a new workspace, makes the current user its Owner, and switches
    /// the user to it.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceRequest request, CancellationToken ct)
    {
        var trimmed = request?.Name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return BadRequest(new { error = "name_required" });
        }
        if (trimmed.Length > 200)
        {
            return BadRequest(new { error = "name_too_long" });
        }

        var userId = _currentUser.GetCurrentUserId();
        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Unauthorized();

        var now = DateTime.UtcNow;
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            OwnerUserId = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Workspaces.Add(workspace);
        _db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = WorkspaceRole.Owner,
            CreatedAt = now,
        });

        user.CurrentWorkspaceId = workspace.Id;
        user.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Created workspace {WorkspaceId} for user {UserId}", workspace.Id, userId);

        return Ok(new
        {
            id = workspace.Id,
            name = workspace.Name,
            role = WorkspaceRole.Owner.ToString(),
            isCurrent = true,
        });
    }

    /// <summary>
    /// Switches the user's current workspace. Verifies WorkspaceMember exists
    /// before mutating the user row — returns 403 otherwise.
    /// </summary>
    [HttpPost("{workspaceId:guid}/switch")]
    public async Task<IActionResult> Switch(Guid workspaceId, CancellationToken ct)
    {
        var userId = _currentUser.GetCurrentUserId();

        var workspace = await (
            from m in _db.WorkspaceMembers
            join w in _db.Workspaces on m.WorkspaceId equals w.Id
            where m.UserId == userId && m.WorkspaceId == workspaceId
            select w
        ).FirstOrDefaultAsync(ct);

        if (workspace is null)
        {
            _logger.LogWarning("User {UserId} attempted to switch to non-member workspace {WorkspaceId}", userId, workspaceId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "not_a_member" });
        }

        var user = await _db.AppUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Unauthorized();

        user.CurrentWorkspaceId = workspace.Id;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            currentWorkspaceId = workspace.Id,
            workspaceName = workspace.Name,
        });
    }
}

public record CreateWorkspaceRequest(string Name);
