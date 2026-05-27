using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

/// <summary>
/// Join row between <see cref="AppUser"/> and <see cref="Workspace"/>.
/// Unique on (WorkspaceId, UserId) so a user cannot be added twice to the
/// same workspace. Role is persisted as a string column.
/// </summary>
public class WorkspaceMember
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid UserId { get; set; }

    public WorkspaceRole Role { get; set; }

    public DateTime CreatedAt { get; set; }
}
