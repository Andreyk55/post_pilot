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
        }

        // Flip the connection row itself. The handler owns assets + posts;
        // the orchestrator owns the connection state machine.
        var connection = await _context.MetaConnections.FindAsync(new object[] { active.Id }, ct);
        if (connection != null && connection.IsConnected)
        {
            var now = DateTime.UtcNow;
            connection.IsConnected = false;
            connection.DisconnectedAt = now;
            connection.UpdatedAt = now;
            await _context.SaveChangesAsync(ct);
        }
    }
}
