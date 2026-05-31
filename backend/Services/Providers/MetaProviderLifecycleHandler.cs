using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Meta-specific disconnect cleanup: soft-disconnect Pages + IG accounts,
/// cancel non-executed posts targeting them, stamp cancellation metadata.
///
/// Token revoke against Graph API stays in <c>MetaOAuthService.DisconnectAsync</c>
/// — the orchestrator doesn't need to know about Meta's HTTP details.
/// </summary>
public class MetaProviderLifecycleHandler : IProviderLifecycleHandler
{
    private readonly AppDbContext _context;
    private readonly IPostScheduler _scheduler;
    private readonly ILogger<MetaProviderLifecycleHandler> _logger;

    public ProviderType Provider => ProviderType.Meta;

    public MetaProviderLifecycleHandler(
        AppDbContext context,
        IPostScheduler scheduler,
        ILogger<MetaProviderLifecycleHandler> logger)
    {
        _context = context;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task DisconnectAssetsAndCancelPostsAsync(
        Guid workspaceId,
        Guid connectionId,
        string? providerAccountId,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var pages = await _context.ConnectedPages
            .Where(p => p.MetaConnectionId == connectionId)
            .ToListAsync(ct);

        var igs = await _context.ConnectedInstagramAccounts
            .Where(i => i.MetaConnectionId == connectionId)
            .ToListAsync(ct);

        var activePageIds = pages.Where(p => p.IsConnected).Select(p => p.Id).ToHashSet();
        var activeIgIds = igs.Where(i => i.IsConnected).Select(i => i.Id).ToHashSet();

        // Cancel non-executed posts FIRST while TargetPageId/TargetInstagramAccountId
        // still reference rows we know about. Executed history (Published/Failed)
        // is left untouched.
        await CancelNonExecutedPostsAsync(
            workspaceId,
            activePageIds,
            activeIgIds,
            providerAccountId,
            now,
            ct);

        // Soft-disconnect assets. Rows survive as FK targets for historical posts.
        foreach (var page in pages.Where(p => p.IsConnected))
        {
            page.IsConnected = false;
            page.DisconnectedAt = now;
        }
        foreach (var ig in igs.Where(i => i.IsConnected))
        {
            ig.IsConnected = false;
            ig.DisconnectedAt = now;
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyCollection<string>> FindAssetsOwnedByOtherWorkspaceAsync(
        Guid workspaceId,
        IEnumerable<string> candidateExternalAssetIds,
        CancellationToken ct)
    {
        var candidates = candidateExternalAssetIds
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet();
        if (candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        // Owned = IsConnected (Active OR ReauthRequired) in a DIFFERENT workspace.
        var ownedPages = await _context.ConnectedPages
            .Where(p => p.WorkspaceId != workspaceId
                     && p.IsConnected
                     && candidates.Contains(p.PageId))
            .Select(p => p.PageId)
            .ToListAsync(ct);

        var ownedIgs = await _context.ConnectedInstagramAccounts
            .Where(i => i.WorkspaceId != workspaceId
                     && i.IsConnected
                     && candidates.Contains(i.IgBusinessId))
            .Select(i => i.IgBusinessId)
            .ToListAsync(ct);

        return ownedPages.Concat(ownedIgs).ToHashSet();
    }

    public async Task SetAssetsStatusAsync(
        Guid connectionId,
        ConnectionStatus status,
        CancellationToken ct)
    {
        // Flip Status on owned asset rows only; never touch IsConnected.
        var pages = await _context.ConnectedPages
            .Where(p => p.MetaConnectionId == connectionId && p.IsConnected)
            .ToListAsync(ct);
        var igs = await _context.ConnectedInstagramAccounts
            .Where(i => i.MetaConnectionId == connectionId && i.IsConnected)
            .ToListAsync(ct);

        if (pages.Count == 0 && igs.Count == 0) return;

        foreach (var p in pages) p.Status = status;
        foreach (var i in igs) i.Status = status;
        await _context.SaveChangesAsync(ct);
    }

    private async Task CancelNonExecutedPostsAsync(
        Guid workspaceId,
        IReadOnlySet<Guid> pageIds,
        IReadOnlySet<Guid> igIds,
        string? providerAccountId,
        DateTime now,
        CancellationToken ct)
    {
        if (pageIds.Count == 0 && igIds.Count == 0) return;

        var affected = await _context.Posts
            .Where(p => p.WorkspaceId == workspaceId)
            // Scheduled / RetryPending / Processing are the "non-executed" states
            // per the product spec. Published / Failed / Canceled stay as-is.
            .Where(p => p.Status == PostStatus.Scheduled
                     || p.Status == PostStatus.RetryPending
                     || p.Status == PostStatus.Processing)
            .Where(p =>
                (p.TargetPageId != null && pageIds.Contains(p.TargetPageId.Value)) ||
                (p.TargetInstagramAccountId != null && igIds.Contains(p.TargetInstagramAccountId.Value)))
            .ToListAsync(ct);

        if (affected.Count == 0) return;

        const string reasonCode = "ProviderDisconnected";
        const string message = "Post canceled because the Meta account was disconnected.";
        var stampedMessage = $"[{reasonCode}] {message}";

        foreach (var post in affected)
        {
            try
            {
                await _scheduler.CancelScheduleAsync(post, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CancelScheduleAsync failed for post {PostId} during Meta disconnect", post.Id);
            }

            post.Status = PostStatus.Canceled;
            post.CanceledAt = now;
            post.UpdatedAt = now;
            post.ScheduleArn = null;
            post.NextRetryAt = null;
            post.ErrorMessage = stampedMessage;
            post.CancellationReason = CancellationReason.ProviderDisconnected;
            post.CanceledBecauseProvider = ProviderType.Meta;
            post.CanceledBecauseProviderAccountId = providerAccountId;
        }

        // Caller's SaveChangesAsync covers these — but flush here too so subsequent
        // queries (e.g. token revoke in MetaOAuthService) see the canceled rows.
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Canceled {Count} non-executed post(s) due to Meta disconnect in workspace {WorkspaceId}",
            affected.Count, workspaceId);
    }
}
