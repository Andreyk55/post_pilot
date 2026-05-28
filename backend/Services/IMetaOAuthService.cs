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
    /// Exchange authorization code for access token and fetch available pages
    /// </summary>
    Task<MetaOAuthCallbackResponse> HandleCallbackAsync(string code, string state);

    /// <summary>
    /// Complete OAuth flow and save connection immediately (identity-level only, no page selection)
    /// </summary>
    Task<MetaOAuthCompleteResponse> CompleteOAuthAsync(string code, string state, Guid userId);

    /// <summary>
    /// Discover Instagram Business accounts linked to selected pages
    /// </summary>
    Task<MetaDiscoverInstagramResponse> DiscoverInstagramAccountsAsync(string tempToken, List<string> pageIds, Guid workspaceId);

    /// <summary>
    /// Save the final connection with selected pages and Instagram accounts
    /// </summary>
    Task<MetaSaveConnectionResponse> SaveConnectionAsync(string tempToken, List<string> selectedPageIds, List<string> selectedInstagramIds, Guid userId);

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
