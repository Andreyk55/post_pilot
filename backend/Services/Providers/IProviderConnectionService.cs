using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Generic provider connection lifecycle. Provider-specific OAuth flows
/// (MetaOAuthService today, future LinkedInOAuthService) call into this service
/// to enforce the product rules:
///
///   1. At most ONE active connection per (workspace, provider).
///   2. Disconnect cancels all non-executed posts and hides history.
///   3. Reconnect of the SAME provider account resurfaces history.
///
/// What stays out of this interface:
///   - OAuth token exchange / refresh / scopes (provider-specific).
///   - Asset discovery (Facebook Pages, IG accounts, …) — handled by the
///     provider OAuth service after it asks this layer to register the identity.
/// </summary>
public interface IProviderConnectionService
{
    /// <summary>
    /// Returns the currently active provider connection for the workspace,
    /// or <c>null</c> if no provider connection is active.
    /// </summary>
    Task<ProviderConnectionInfo?> GetActiveConnectionAsync(
        Guid workspaceId,
        ProviderType provider,
        CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="ProviderAlreadyConnectedException"/> if the workspace
    /// already has an active connection for <paramref name="provider"/>. No-op
    /// otherwise. Provider OAuth services call this BEFORE persisting any
    /// connection state to ensure the spec's "reject second account" rule.
    /// </summary>
    Task EnsureCanConnectAsync(
        Guid workspaceId,
        ProviderType provider,
        CancellationToken ct = default);

    /// <summary>
    /// Disconnect the currently active provider connection for the workspace:
    /// soft-disconnect the connection row, soft-disconnect its assets, cancel
    /// non-executed posts, and stamp cancellation metadata.
    ///
    /// No-op if no active connection exists (idempotent). Rows are NEVER
    /// hard-deleted so historical Posts keep their FK targets and so the
    /// "reconnect same account ⇒ resurface history" rule can work later.
    /// </summary>
    Task DisconnectAsync(
        Guid workspaceId,
        ProviderType provider,
        CancellationToken ct = default);
}
