using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services;

public interface IMetaOAuthService
{
    /// <summary>
    /// Generate OAuth authorization URL and create state record
    /// </summary>
    Task<MetaOAuthStartResponse> StartOAuthAsync();

    /// <summary>
    /// Exchange authorization code for access token and fetch available pages
    /// </summary>
    Task<MetaOAuthCallbackResponse> HandleCallbackAsync(string code, string state);

    /// <summary>
    /// Discover Instagram Business accounts linked to selected pages
    /// </summary>
    Task<MetaDiscoverInstagramResponse> DiscoverInstagramAccountsAsync(string tempToken, List<string> pageIds);

    /// <summary>
    /// Save the final connection with selected pages and Instagram accounts
    /// </summary>
    Task<MetaSaveConnectionResponse> SaveConnectionAsync(string tempToken, List<string> selectedPageIds, List<string> selectedInstagramIds);

    /// <summary>
    /// Get current Meta connection for the user
    /// </summary>
    Task<MetaConnectionResponse> GetConnectionAsync(Guid userId);

    /// <summary>
    /// Get available pages using stored tokens (for manage flow)
    /// </summary>
    Task<MetaAvailablePagesResponse> GetAvailablePagesAsync(Guid userId);

    /// <summary>
    /// Update selected pages and Instagram accounts
    /// </summary>
    Task<MetaSaveConnectionResponse> UpdateConnectionAsync(Guid userId, List<string> selectedPageIds, List<string> selectedInstagramIds);

    /// <summary>
    /// Disconnect Meta - revoke tokens and remove connection
    /// </summary>
    Task DisconnectAsync(Guid userId);
}
