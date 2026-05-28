namespace PostPilot.Api.Services.Auth;

/// <summary>
/// Resolves and validates the current user's active workspace for the request.
/// Always re-checks WorkspaceMember in the DB — never trusts a frontend-supplied
/// workspace id and never trusts a claim alone. If the user's
/// <c>AppUser.CurrentWorkspaceId</c> is missing or no longer valid (membership
/// revoked, workspace deleted) the provider self-heals by picking the first
/// workspace the user is a member of, or throws if there is none.
/// </summary>
public interface ICurrentWorkspaceProvider
{
    /// <summary>
    /// Returns the validated current workspace id for the current user.
    /// Throws <see cref="UnauthorizedAccessException"/> if there is no user
    /// or the user has no workspace memberships at all.
    /// </summary>
    Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="GetCurrentWorkspaceIdAsync"/> but also returns the workspace name
    /// for endpoints that need both. Saves a second round-trip.
    /// </summary>
    Task<CurrentWorkspaceInfo> GetCurrentWorkspaceAsync(CancellationToken ct = default);
}

public record CurrentWorkspaceInfo(Guid UserId, Guid WorkspaceId, string WorkspaceName);
