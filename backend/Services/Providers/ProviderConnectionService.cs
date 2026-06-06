using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Default implementation backed by <see cref="MetaConnection"/> rows. Today
/// every provider lifecycle goes through this single table; if a future
/// provider needs its own entity, only this class and the snapshot need to
/// learn about it.
/// </summary>
public class ProviderConnectionService : IProviderConnectionService
{
    private readonly AppDbContext _context;
    private readonly IReadOnlyDictionary<ProviderType, IProviderLifecycleHandler> _handlers;
    private readonly ILogger<ProviderConnectionService> _logger;

    public ProviderConnectionService(
        AppDbContext context,
        IEnumerable<IProviderLifecycleHandler> handlers,
        ILogger<ProviderConnectionService> logger)
    {
        _context = context;
        // Last-registration wins — fine because we expect one handler per provider.
        _handlers = handlers.ToDictionary(h => h.Provider, h => h);
        _logger = logger;
    }

    public async Task<ProviderConnectionInfo?> GetActiveConnectionAsync(
        Guid workspaceId,
        ProviderType provider,
        CancellationToken ct = default)
    {
        var row = await _context.MetaConnections
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId
                     && c.Provider == provider
                     && c.IsConnected)
            .Select(c => new ProviderConnectionInfo(
                c.Id,
                c.WorkspaceId,
                c.Provider,
                c.ProviderAccountId,
                c.ProviderAccountName,
                c.UserId,
                c.ConnectedAt))
            .FirstOrDefaultAsync(ct);

        return row;
    }

    public async Task EnsureCanConnectAsync(
        Guid workspaceId,
        ProviderType provider,
        CancellationToken ct = default)
    {
        var alreadyActive = await _context.MetaConnections
            .AnyAsync(c => c.WorkspaceId == workspaceId
                        && c.Provider == provider
                        && c.IsConnected, ct);

        if (alreadyActive)
        {
            throw new ProviderAlreadyConnectedException(provider);
        }
    }

    public async Task EnsureNotOwnedByAnotherWorkspaceAsync(
        Guid workspaceId,
        ProviderType provider,
        string? externalAccountId,
        IEnumerable<string> externalAssetIds,
        CancellationToken ct = default)
    {
        // 1. Account-level ownership is PERMANENT: a provider account identity
        //    (Provider + ExternalAccountId) belongs forever to the FIRST workspace
        //    that connected it. We therefore check ALL rows in other workspaces —
        //    connected AND disconnected. Disconnecting in the owning workspace does
        //    NOT release the identity, so it can never be claimed by anyone else.
        if (!string.IsNullOrEmpty(externalAccountId))
        {
            var accountOwnedElsewhere = await _context.MetaConnections
                .AnyAsync(c => c.WorkspaceId != workspaceId
                            && c.Provider == provider
                            && c.ProviderAccountId == externalAccountId, ct);

            if (accountOwnedElsewhere)
            {
                _logger.LogWarning(
                    "Connect blocked: provider {Provider} account {ExternalAccountId} is permanently owned by another workspace (requesting workspace {WorkspaceId}).",
                    provider, externalAccountId, workspaceId);
                throw new ProviderOwnedByAnotherWorkspaceException(provider, externalAccountId);
            }
        }

        // 2. Asset-level ownership: any page / IG account owned by a different workspace.
        //    Delegated to the provider handler since asset tables are provider-specific.
        var assetIds = externalAssetIds?.ToList() ?? new List<string>();
        if (assetIds.Count > 0 && _handlers.TryGetValue(provider, out var handler))
        {
            var conflicting = await handler.FindAssetsOwnedByOtherWorkspaceAsync(workspaceId, assetIds, ct);
            if (conflicting.Count > 0)
            {
                _logger.LogWarning(
                    "Connect blocked: provider {Provider} asset(s) {Assets} owned by another workspace (requesting workspace {WorkspaceId}).",
                    provider, string.Join(",", conflicting), workspaceId);
                throw new ProviderOwnedByAnotherWorkspaceException(provider, conflicting.First());
            }
        }
    }

    public async Task EnsureAccountMatchesWorkspaceBindingAsync(
        Guid workspaceId,
        ProviderType provider,
        string? incomingAccountId,
        CancellationToken ct = default)
    {
        // Identity unresolved this time — can't enforce the binding. The looser
        // active-connection / cross-workspace guards still apply elsewhere.
        if (string.IsNullOrEmpty(incomingAccountId))
        {
            return;
        }

        // The workspace's permanent identity for this provider = any row (connected
        // OR disconnected) carrying a non-null ProviderAccountId. Once one exists,
        // it pins the workspace to that account forever.
        var boundAccountId = await _context.MetaConnections
            .AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId
                     && c.Provider == provider
                     && c.ProviderAccountId != null
                     && c.ProviderAccountId != "")
            .Select(c => c.ProviderAccountId)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrEmpty(boundAccountId) && boundAccountId != incomingAccountId)
        {
            _logger.LogWarning(
                "Connect blocked: workspace {WorkspaceId} is permanently bound to {Provider} account {BoundAccountId} " +
                "but attempted to connect a different account {AttemptedAccountId}.",
                workspaceId, provider, boundAccountId, incomingAccountId);
            throw new ProviderAccountMismatchException(provider, boundAccountId, incomingAccountId);
        }
    }

    public async Task MarkReauthRequiredAsync(
        Guid workspaceId,
        ProviderType provider,
        CancellationToken ct = default)
    {
        var active = await _context.MetaConnections
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId
                                   && c.Provider == provider
                                   && c.IsConnected, ct);

        if (active == null)
        {
            // No owning connection — nothing to flag. Idempotent.
            return;
        }

        if (active.Status != ConnectionStatus.ReauthRequired)
        {
            active.Status = ConnectionStatus.ReauthRequired;
            active.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Marked {Provider} connection {ConnectionId} (workspace {WorkspaceId}) as ReauthRequired. " +
                "Ownership retained; posts remain visible.",
                provider, active.Id, workspaceId);
        }

        // Mirror onto assets so the UI can flag the affected page/IG account.
        if (_handlers.TryGetValue(provider, out var handler))
        {
            await handler.SetAssetsStatusAsync(active.Id, ConnectionStatus.ReauthRequired, ct);
        }
    }

    public async Task DisconnectAsync(
        Guid workspaceId,
        ProviderType provider,
        CancellationToken ct = default)
    {
        var active = await _context.MetaConnections
            .Where(c => c.WorkspaceId == workspaceId
                     && c.Provider == provider
                     && c.IsConnected)
            .Select(c => new { c.Id, c.ProviderAccountId })
            .FirstOrDefaultAsync(ct);

        if (active == null)
        {
            // Already disconnected — idempotent.
            return;
        }

        if (!_handlers.TryGetValue(provider, out var handler))
        {
            // No handler registered means we can still flip the connection row
            // off, but we can't sweep provider-specific assets/posts. Loud log
            // so a future provider doesn't silently misbehave in dev.
            _logger.LogError(
                "No IProviderLifecycleHandler registered for provider {Provider} — " +
                "disconnect will mark the connection inactive but skip asset/post cleanup.",
                provider);
        }
        else
        {
            await handler.DisconnectAssetsAndCancelPostsAsync(
                workspaceId,
                active.Id,
                active.ProviderAccountId,
                ct);

            // Wipe asset/page-level secrets (e.g. per-page access tokens) so a stale
            // credential can never be used to publish after disconnect. Identity
            // columns on the assets are preserved. No-op for providers without
            // asset-level secrets.
            await handler.ClearStoredAssetCredentialsAsync(active.Id, ct);
        }

        // Flip the connection row itself. The handler owns assets + posts;
        // the orchestrator owns the connection state machine.
        var connection = await _context.MetaConnections.FindAsync(new object[] { active.Id }, ct);
        if (connection != null && connection.IsConnected)
        {
            var now = DateTime.UtcNow;
            connection.IsConnected = false;
            // Reset the reauth refinement: a disconnected row carries no live ownership,
            // so leaving it ReauthRequired would be meaningless. A future reactivation
            // starts from Active.
            connection.Status = ConnectionStatus.Active;
            connection.DisconnectedAt = now;
            connection.UpdatedAt = now;
            // Clear the stored credential. The identity (WorkspaceId, Provider,
            // ProviderAccountId, ProviderAccountName) is kept so the same account can
            // reconnect and resurface history; only the secret is wiped. A reconnect
            // overwrites AccessToken with a fresh token.
            connection.AccessToken = null;
            await _context.SaveChangesAsync(ct);
        }
    }
}
