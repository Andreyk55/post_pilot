namespace PostPilot.Api.Entities;

/// <summary>
/// Tenant boundary that future data (posts, connections, media) will be
/// scoped to. Today every AppUser gets exactly one default workspace which
/// they own. Workspace switching / multi-membership is not yet exposed in
/// the UI but the schema supports it via <see cref="WorkspaceMember"/>.
/// </summary>
public class Workspace
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public Guid OwnerUserId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
