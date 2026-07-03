namespace PostPilot.Api.Services;

/// <summary>
/// Thrown when a Meta OAuth <c>state</c> / temp-token is consumed from a workspace context that
/// does not match the workspace the state was minted for. The "current workspace" is resolved
/// server-side via <see cref="Auth.ICurrentWorkspaceProvider"/> (which re-checks the caller's
/// membership), so a mismatch means the caller is either not a member of the state's workspace
/// or has since switched their selected workspace. Either way the flow must be rejected (403).
///
/// <para>Carries no caller-facing detail beyond a fixed message so a probe cannot learn which
/// workspace a state belongs to.</para>
/// </summary>
public sealed class OAuthStateAccessDeniedException : Exception
{
    public const string UserMessage =
        "This connection flow does not belong to your current workspace. Start the connection again from the workspace you want to connect.";

    /// <summary>Workspace the state was minted for (for server-side logging only — never returned to the caller).</summary>
    public Guid StateWorkspaceId { get; }

    /// <summary>Workspace the caller is currently operating in (server-resolved, membership-checked).</summary>
    public Guid CurrentWorkspaceId { get; }

    public OAuthStateAccessDeniedException(Guid stateWorkspaceId, Guid currentWorkspaceId)
        : base(UserMessage)
    {
        StateWorkspaceId = stateWorkspaceId;
        CurrentWorkspaceId = currentWorkspaceId;
    }
}
