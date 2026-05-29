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
}
