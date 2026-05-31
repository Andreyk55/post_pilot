namespace PostPilot.Api.Services.Auth;

/// <summary>
/// Thrown when the authenticated user has no usable current/selected workspace:
/// <c>AppUser.CurrentWorkspaceId</c> is missing, points at a deleted workspace, or
/// points at a workspace the user is no longer a member of. The caller MUST stop —
/// we deliberately do NOT fall back to another workspace, because in the MVP each
/// workspace can have a different connected provider account and silently choosing
/// a different one would land media/posts/provider actions in the wrong account.
///
/// <para>Maps to HTTP 409 Conflict: the client must (re)select a valid workspace
/// before retrying.</para>
/// </summary>
public class WorkspaceNotSelectedException : Exception
{
    public WorkspaceNotSelectedException(string message) : base(message) { }
}

/// <summary>
/// Thrown when a workspace id is resolved but the authenticated user is not a
/// <c>WorkspaceMember</c> of it. Maps to HTTP 403 Forbidden.
/// </summary>
public class WorkspaceAccessDeniedException : Exception
{
    public WorkspaceAccessDeniedException(string message) : base(message) { }
}
