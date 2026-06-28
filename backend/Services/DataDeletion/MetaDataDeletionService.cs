using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Default <see cref="IMetaDataDeletionService"/>. See the interface for scope.
///
/// FK-aware delete order (Postgres enforces these; in-memory does not, but we honor
/// the same order so the production path is exercised by tests):
///   1. Cancel pending schedules (external EventBridge) first.
///   2. PostMediaItem (cascade child of Post — deleted explicitly for provider parity).
///   3. Post  — Post→Page/IG is RESTRICT, so posts must go before their targets.
///   4. ConnectedPage / ConnectedInstagramAccount — MetaConnection→assets is SET NULL,
///      not cascade, so assets are removed explicitly.
///   5. MetaConnection.
///   6. MetaOAuthState, Media (no FK from Post; removed explicitly).
///   7. Storage objects — external, best-effort, AFTER the DB commit.
/// </summary>
public sealed class MetaDataDeletionService : IMetaDataDeletionService
{
    private readonly AppDbContext _context;
    private readonly IPostScheduler _scheduler;
    private readonly IStorageDeletionService _storageDeletion;
    private readonly ILogger<MetaDataDeletionService> _logger;

    public MetaDataDeletionService(
        AppDbContext context,
        IPostScheduler scheduler,
        IStorageDeletionService storageDeletion,
        ILogger<MetaDataDeletionService> logger)
    {
        _context = context;
        _scheduler = scheduler;
        _storageDeletion = storageDeletion;
        _logger = logger;
    }

    public async Task<MetaDataDeletionResult> PurgeByProviderAccountIdAsync(
        string? providerAccountId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(providerAccountId))
        {
            // Unknown/empty id is a safe no-op — there is nothing to purge.
            return MetaDataDeletionResult.AlreadyDeleted();
        }

        // Lookup is intentionally UNSCOPED: across all workspaces, connected AND
        // disconnected. (Provider, ProviderAccountId) is unique, so at most one row.
        var connection = await _context.MetaConnections
            .FirstOrDefaultAsync(c =>
                c.Provider == ProviderType.Meta &&
                c.ProviderAccountId == providerAccountId, ct);

        if (connection is null)
        {
            _logger.LogInformation(
                "Meta purge: no connection for the provided account id — treating as already deleted.");
            return MetaDataDeletionResult.AlreadyDeleted();
        }

        var connectionId = connection.Id;
        var workspaceId = connection.WorkspaceId;
        var userId = connection.UserId;

        // Storage prefixes scoped to exactly this user+workspace's Meta provider folders.
        var allowedPrefixes = MetaStoragePrefixes(userId, workspaceId);

        // ── Gather (all workspace-scoped Meta data) ──────────────────────────────
        // Pages/IG tables are inherently Meta and the workspace is permanently bound
        // to a single Meta account, so workspace scope == this connection's assets.
        var pages = await _context.ConnectedPages
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync(ct);
        var igs = await _context.ConnectedInstagramAccounts
            .Where(i => i.WorkspaceId == workspaceId)
            .ToListAsync(ct);

        // Meta posts only (Facebook/Instagram) — never future LinkedIn/non-Meta posts.
        var posts = await _context.Posts
            .Where(p => p.WorkspaceId == workspaceId &&
                        (p.Platform == Platform.Facebook || p.Platform == Platform.Instagram))
            .ToListAsync(ct);
        var postIds = posts.Select(p => p.Id).ToHashSet();

        var postMediaItems = postIds.Count == 0
            ? new List<PostMediaItem>()
            : await _context.PostMediaItems
                .Where(m => postIds.Contains(m.PostId))
                .ToListAsync(ct);

        var oauthStates = await _context.MetaOAuthStates
            .Where(s => s.WorkspaceId == workspaceId)
            .ToListAsync(ct);

        // Media rows are scoped by the Meta provider prefixes (never non-Meta media).
        var media = await _context.Media
            .Where(m => m.WorkspaceId == workspaceId)
            .ToListAsync(ct);
        media = media.Where(m => StartsWithAny(m.StorageKey, allowedPrefixes)).ToList();

        // Collect storage keys BEFORE deleting rows (StorageKey + IG derivative + post media).
        var storageKeys = CollectStorageKeys(media, posts, postMediaItems);

        // ── Cancel pending schedules (external) BEFORE deleting rows ─────────────
        var warnings = new List<string>();
        await CancelPendingSchedulesAsync(posts, warnings, ct);

        // ── DB cleanup (transactional on relational providers) ───────────────────
        var counts = new Dictionary<string, int>();
        var tx = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            _context.PostMediaItems.RemoveRange(postMediaItems);
            _context.Posts.RemoveRange(posts);
            _context.ConnectedPages.RemoveRange(pages);
            _context.ConnectedInstagramAccounts.RemoveRange(igs);
            _context.MetaConnections.Remove(connection);
            _context.MetaOAuthStates.RemoveRange(oauthStates);
            _context.Media.RemoveRange(media);

            await _context.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        catch
        {
            if (tx is not null) await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }

        counts["Posts"] = posts.Count;
        counts["PostMediaItems"] = postMediaItems.Count;
        counts["ConnectedPages"] = pages.Count;
        counts["ConnectedInstagramAccounts"] = igs.Count;
        counts["MetaConnections"] = 1;
        counts["MetaOAuthStates"] = oauthStates.Count;
        counts["Media"] = media.Count;

        // ── Storage objects (external, best-effort, AFTER commit) ────────────────
        var storageResult = await _storageDeletion.DeleteObjectsBestEffortAsync(
            storageKeys, allowedPrefixes, ct);
        counts["StorageObjects"] = storageResult.Deleted;
        warnings.AddRange(storageResult.Warnings);
        if (storageResult.SkippedUnsafe > 0)
        {
            warnings.Add($"{storageResult.SkippedUnsafe} storage object(s) skipped by prefix guard.");
        }

        _logger.LogInformation(
            "Meta purge completed for workspace {WorkspaceId}: posts={Posts}, pages={Pages}, igs={Igs}, " +
            "media={Media}, oauthStates={States}, storageDeleted={Storage}.",
            workspaceId, counts["Posts"], counts["ConnectedPages"], counts["ConnectedInstagramAccounts"],
            counts["Media"], counts["MetaOAuthStates"], counts["StorageObjects"]);

        return new MetaDataDeletionResult(
            DataDeletionStatus.Completed, userId, workspaceId, counts, warnings);
    }

    private async Task CancelPendingSchedulesAsync(
        List<Post> posts, List<string> warnings, CancellationToken ct)
    {
        // Cancel anything that may still hold an active EventBridge schedule/job.
        var pending = posts.Where(p =>
            p.Status is PostStatus.Scheduled
                     or PostStatus.RetryPending
                     or PostStatus.Processing
                     or PostStatus.Publishing
            || !string.IsNullOrEmpty(p.ScheduleArn));

        foreach (var post in pending)
        {
            try
            {
                await _scheduler.CancelScheduleAsync(post, ct);
            }
            catch (Exception ex)
            {
                // A missing/already-gone schedule must not break idempotency.
                warnings.Add($"Schedule cancel failed for a post; continuing.");
                _logger.LogWarning(ex,
                    "CancelScheduleAsync failed for post {PostId} during Meta purge", post.Id);
            }
        }
    }

    /// <summary>StorageKey + Instagram derivative key from media, plus post-level media urls.</summary>
    private static List<string?> CollectStorageKeys(
        List<Entities.Media> media, List<Post> posts, List<PostMediaItem> postMediaItems)
    {
        var keys = new List<string?>();
        foreach (var m in media)
        {
            keys.Add(m.StorageKey);
            keys.Add(m.InstagramImageStorageKey);
            keys.Add(m.ThumbnailStorageKey);
        }
        foreach (var p in posts)
        {
            keys.Add(p.MediaUrl);
            keys.Add(p.SelectedThumbnailUrl);
        }
        foreach (var item in postMediaItems)
        {
            keys.Add(item.MediaUrl);
        }
        return keys;
    }

    internal static IReadOnlyCollection<string> MetaStoragePrefixes(Guid userId, Guid workspaceId) => new[]
    {
        $"users/{userId:D}/workspaces/{workspaceId:D}/providers/meta-facebook/",
        $"users/{userId:D}/workspaces/{workspaceId:D}/providers/meta-instagram/",
    };

    private static bool StartsWithAny(string? value, IReadOnlyCollection<string> prefixes)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
