using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services;

public interface IMetaOAuthService
{
    /// <summary>
    /// Generate OAuth authorization URL and create state record bound to the workspace
    /// that will receive the resulting MetaConnection.
    /// </summary>
    Task<MetaOAuthStartResponse> StartOAuthAsync(Guid workspaceId);

    /// <summary>
    /// Exchange authorization code for access token and fetch available pages.
    /// <paramref name="currentWorkspaceId"/> is the caller's server-resolved (membership-checked)
    /// current workspace; the state MUST have been minted for that same workspace or the flow is
    /// rejected (see <see cref="OAuthStateAccessDeniedException"/>).
    /// </summary>
    Task<MetaOAuthCallbackResponse> HandleCallbackAsync(string code, string state, Guid currentWorkspaceId);

    /// <summary>
    /// Complete OAuth flow and save connection immediately (identity-level only, no page selection).
    /// <paramref name="currentWorkspaceId"/> must match the state's workspace (membership-checked).
    /// </summary>
    Task<MetaOAuthCompleteResponse> CompleteOAuthAsync(string code, string state, Guid userId, Guid currentWorkspaceId);

    /// <summary>
    /// Discover Instagram Business accounts linked to selected pages. When <paramref name="tempToken"/>
    /// is an OAuth state id, that state MUST belong to <paramref name="workspaceId"/> (the caller's
    /// current workspace); otherwise the flow is rejected.
    /// </summary>
    Task<MetaDiscoverInstagramResponse> DiscoverInstagramAccountsAsync(string tempToken, List<string> pageIds, Guid workspaceId);

    /// <summary>
    /// Save the final connection with selected pages and Instagram accounts.
    /// <paramref name="currentWorkspaceId"/> must match the temp-token state's workspace (membership-checked).
    /// </summary>
    Task<MetaSaveConnectionResponse> SaveConnectionAsync(string tempToken, List<string> selectedPageIds, List<string> selectedInstagramIds, Guid userId, Guid currentWorkspaceId);

    /// <summary>
    /// Get current Meta connection for the workspace
    /// </summary>
    Task<MetaConnectionResponse> GetConnectionAsync(Guid workspaceId);

    /// <summary>
    /// Get available pages using stored tokens (for manage flow)
    /// </summary>
    Task<MetaAvailablePagesResponse> GetAvailablePagesAsync(Guid workspaceId);

    /// <summary>
    /// Update selected pages and Instagram accounts
    /// </summary>
    Task<MetaSaveConnectionResponse> UpdateConnectionAsync(Guid workspaceId, List<string> selectedPageIds, List<string> selectedInstagramIds);

    /// <summary>
    /// Idempotent repair: re-promote Instagram accounts linked to currently-connected
    /// Facebook Pages to connected publishable assets, without changing page selection.
    /// Fixes pre-existing rows where a linked IG never became a connected asset.
    /// </summary>
    Task<MetaSaveConnectionResponse> RefreshAssetsAsync(Guid workspaceId);

    /// <summary>
    /// Disconnect Meta - revoke tokens and remove connection for this workspace
    /// </summary>
    Task DisconnectAsync(Guid workspaceId);

    /// <summary>
    /// Discover Instagram eligibility for all connected Facebook Pages.
    /// Returns per-page breakdown with status (Connected, NotLinked, etc.)
    /// </summary>
    Task<InstagramDiscoveryResponse> DiscoverInstagramEligibilityAsync(Guid workspaceId);

    /// <summary>
    /// Debug: returns raw Graph API responses for Instagram discovery diagnostics
    /// </summary>
    Task<object> DebugInstagramDiscoveryAsync(Guid workspaceId);
}
