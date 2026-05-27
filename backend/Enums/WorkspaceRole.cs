namespace PostPilot.Api.Enums;

/// <summary>
/// Membership role inside a Workspace. Only Owner is used today —
/// Admin/Member exist so future membership/invite flows don't require a
/// schema migration.
/// </summary>
public enum WorkspaceRole
{
    Owner,
    Admin,
    Member,
}
