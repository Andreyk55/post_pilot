namespace PostPilot.Api.Services.Auth;

/// <summary>
/// Resolves and validates the current user's explicitly-selected workspace for the
/// request. Always re-checks WorkspaceMember in the DB — never trusts a frontend-supplied
/// workspace id and never trusts a claim alone.
///
/// <para>This resolver NEVER silently falls back to another workspace. In the MVP a user
/// can belong to several workspaces, each with its own connected provider account, so
/// guessing a workspace could land media/posts/provider actions in the wrong account.
/// If the selected workspace is missing, stale, deleted, or unauthorized it throws and
/// the caller must stop.</para>
///
/// <para>Failure modes:
/// <list type="bullet">
///   <item><see cref="WorkspaceNotSelectedException"/> (→ 409) when
///   <c>AppUser.CurrentWorkspaceId</c> is null or points at a workspace that no longer
///   exists.</item>
///   <item><see cref="WorkspaceAccessDeniedException"/> (→ 403) when the selected
///   workspace exists but the user is not a member.</item>
///   <item><see cref="UnauthorizedAccessException"/> (→ 401) when there is no
///   authenticated user.</item>
/// </list>
/// </para>
/// </summary>
public interface ICurrentWorkspaceProvider
{
    /// <summary>
    /// Returns the validated, explicitly-selected current workspace id for the user.
    /// Throws per the failure modes documented on <see cref="ICurrentWorkspaceProvider"/>;
    /// never auto-selects a workspace.
    /// </summary>
    Task<Guid> GetCurrentWorkspaceIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="GetCurrentWorkspaceIdAsync"/> but also returns the workspace name
    /// for endpoints that need both. Saves a second round-trip. Same failure modes.
    /// </summary>
    Task<CurrentWorkspaceInfo> GetCurrentWorkspaceAsync(CancellationToken ct = default);
}

public record CurrentWorkspaceInfo(Guid UserId, Guid WorkspaceId, string WorkspaceName);
