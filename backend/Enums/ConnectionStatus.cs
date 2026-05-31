namespace PostPilot.Api.Enums;

/// <summary>
/// Refines the "owned" state of a provider connection / asset (page, IG account).
///
/// This sits ALONGSIDE the existing <c>IsConnected</c> boolean, which remains the
/// owned-vs-released axis:
///
///   - <c>IsConnected == true</c>  → the workspace OWNS the account/asset. Ownership
///     blocks any other workspace from connecting the same provider account/asset.
///     The refinement (<see cref="Active"/> vs <see cref="ReauthRequired"/>) says
///     whether the stored token still works.
///   - <c>IsConnected == false</c> → ownership RELEASED (a real user-initiated
///     disconnect). Another workspace may now connect the same account/asset.
///
/// Only a real Disconnect (<c>IsConnected = false</c>) releases ownership. A token
/// becoming invalid / expired / session-invalidated flips the status to
/// <see cref="ReauthRequired"/> but keeps <c>IsConnected = true</c> — ownership is
/// retained, posts stay visible, and the UI shows a reconnect action.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>Owned and the stored token is believed valid. Default for owned rows.</summary>
    Active = 0,

    /// <summary>
    /// Owned but the stored token is invalid/expired/session-invalidated. The user
    /// must reconnect in the SAME workspace. Still blocks other workspaces from
    /// connecting the same provider account/asset. NeedsReauth is treated as an
    /// equivalent non-disconnected (owning) state.
    /// </summary>
    ReauthRequired = 1,
}
