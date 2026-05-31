using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Provider-specific work executed by <see cref="IProviderConnectionService"/>.
/// One handler per <see cref="ProviderType"/>, registered via DI. Today only the
/// Meta handler exists; LinkedIn/X/TikTok will add their own without touching
/// the generic orchestrator.
///
/// The orchestrator handles uniqueness/identity rules; the handler handles
/// the provider's own assets and post-cancellation semantics.
/// </summary>
public interface IProviderLifecycleHandler
{
    ProviderType Provider { get; }

    /// <summary>
    /// Soft-disconnect every asset that belongs to <paramref name="connectionId"/>
    /// (pages, IG accounts, etc.) and cancel any non-executed posts targeting them.
    ///
    /// Implementations MUST:
    ///   - mark each asset row IsConnected=false / DisconnectedAt=now,
    ///   - cancel Scheduled / RetryPending / Processing posts targeting those
    ///     assets with <see cref="CancellationReason.ProviderDisconnected"/>,
    ///     stamping <c>CanceledBecauseProvider</c> and
    ///     <c>CanceledBecauseProviderAccountId</c>,
    ///   - NEVER hard-delete rows (Posts FK them as history).
    /// </summary>
    Task DisconnectAssetsAndCancelPostsAsync(
        Guid workspaceId,
        Guid connectionId,
        string? providerAccountId,
        CancellationToken ct);

    /// <summary>
    /// Returns the set of external asset ids (e.g. Facebook Page ids, IG account
    /// ids) from <paramref name="candidateExternalAssetIds"/> that are currently
    /// OWNED (IsConnected = true — Active or ReauthRequired) by a workspace OTHER
    /// than <paramref name="workspaceId"/>. Used by the generic cross-workspace
    /// ownership guard. Returns an empty set if nothing conflicts.
    /// </summary>
    Task<IReadOnlyCollection<string>> FindAssetsOwnedByOtherWorkspaceAsync(
        Guid workspaceId,
        IEnumerable<string> candidateExternalAssetIds,
        CancellationToken ct);

    /// <summary>
    /// Mirror the connection's reauth state onto its asset rows: set every owned
    /// (IsConnected = true) asset belonging to <paramref name="connectionId"/> to
    /// the given <paramref name="status"/>. Used so the UI can flag a page/IG
    /// account that needs reconnect. Never changes IsConnected.
    /// </summary>
    Task SetAssetsStatusAsync(
        Guid connectionId,
        ConnectionStatus status,
        CancellationToken ct);
}
