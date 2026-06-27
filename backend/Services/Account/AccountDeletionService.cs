using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.DataDeletion;
using PostPilot.Api.Services.Scheduling;

namespace PostPilot.Api.Services.Account;

/// <summary>
/// Default <see cref="IAccountDeletionService"/>.
///
/// MVP ownership model: one PostPilot user owns their workspace(s). We delete only
/// workspaces whose <see cref="Workspace.OwnerUserId"/> is the authenticated user, all
/// data inside them, the user's memberships everywhere, and finally the AppUser row
/// itself. AppUser carries the auth identity (AuthProvider + ExternalAuthUserId), so
/// there is no separate Account/Auth table to delete — removing AppUser removes both.
///
/// FK-aware delete order mirrors <see cref="MetaDataDeletionService"/> and additionally
/// removes voice profiles, memberships, workspaces, and the user. Workspace→Owner is
/// RESTRICT, so every owned workspace is emptied and removed before the AppUser.
/// </summary>
public sealed class AccountDeletionService : IAccountDeletionService
{
    private readonly AppDbContext _context;
    private readonly IPostScheduler _scheduler;
    private readonly IStorageDeletionService _storageDeletion;
    private readonly ILogger<AccountDeletionService> _logger;

    public AccountDeletionService(
        AppDbContext context,
        IPostScheduler scheduler,
        IStorageDeletionService storageDeletion,
        ILogger<AccountDeletionService> logger)
    {
        _context = context;
        _scheduler = scheduler;
        _storageDeletion = storageDeletion;
        _logger = logger;
    }

    public async Task DeleteCurrentAccountAsync(Guid authenticatedUserId, CancellationToken ct)
    {
        var user = await _context.AppUsers.FirstOrDefaultAsync(u => u.Id == authenticatedUserId, ct);
        if (user is null)
        {
            // Idempotent: nothing to delete (e.g. retried after a prior success).
            _logger.LogInformation("Account deletion: user already gone — no-op.");
            return;
        }

        var ownedWorkspaceIds = await _context.Workspaces
            .Where(w => w.OwnerUserId == authenticatedUserId)
            .Select(w => w.Id)
            .ToListAsync(ct);

        // ── Gather everything inside owned workspaces ────────────────────────────
        var posts = await _context.Posts
            .Where(p => ownedWorkspaceIds.Contains(p.WorkspaceId))
            .ToListAsync(ct);
        var postIds = posts.Select(p => p.Id).ToHashSet();

        var postMediaItems = await _context.PostMediaItems
            .Where(m => ownedWorkspaceIds.Contains(m.WorkspaceId))
            .ToListAsync(ct);

        var pages = await _context.ConnectedPages
            .Where(p => ownedWorkspaceIds.Contains(p.WorkspaceId))
            .ToListAsync(ct);
        var igs = await _context.ConnectedInstagramAccounts
            .Where(i => ownedWorkspaceIds.Contains(i.WorkspaceId))
            .ToListAsync(ct);
        var connections = await _context.MetaConnections
            .Where(c => ownedWorkspaceIds.Contains(c.WorkspaceId))
            .ToListAsync(ct);
        var oauthStates = await _context.MetaOAuthStates
            .Where(s => ownedWorkspaceIds.Contains(s.WorkspaceId))
            .ToListAsync(ct);
        var media = await _context.Media
            .Where(m => ownedWorkspaceIds.Contains(m.WorkspaceId))
            .ToListAsync(ct);
        var voiceProfiles = await _context.AiVoiceProfiles
            .Where(v => ownedWorkspaceIds.Contains(v.WorkspaceId))
            .ToListAsync(ct);

        // Memberships to remove: every member of an owned workspace (the workspace is
        // being deleted) PLUS this user's own memberships in any other workspace.
        var memberships = await _context.WorkspaceMembers
            .Where(m => ownedWorkspaceIds.Contains(m.WorkspaceId) || m.UserId == authenticatedUserId)
            .ToListAsync(ct);

        var workspaces = await _context.Workspaces
            .Where(w => ownedWorkspaceIds.Contains(w.Id))
            .ToListAsync(ct);

        // Support ("Contact Us") messages owned by THIS user. Scoped strictly by UserId so
        // we never touch another user's requests. Removed explicitly (in addition to the
        // cascade FK) so the same behavior holds on the in-memory test provider, which does
        // not enforce cascades. Deliberately NOT scoped by workspace — a support request can
        // have a null/foreign WorkspaceId yet still belong to this user.
        var supportRequests = await _context.SupportContactRequests
            .Where(s => s.UserId == authenticatedUserId)
            .ToListAsync(ct);

        // All bucket files for this user live under this single prefix. The guard makes
        // it impossible to touch another user's objects even if a stray key slipped in.
        var allowedPrefixes = new[] { $"users/{authenticatedUserId:D}/" };
        var storageKeys = CollectStorageKeys(media, posts, postMediaItems);

        // ── Cancel pending schedules (external) BEFORE deleting rows ─────────────
        await CancelPendingSchedulesAsync(posts, ct);

        // ── DB cleanup (transactional on relational providers) ───────────────────
        var tx = _context.Database.IsRelational()
            ? await _context.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            _context.PostMediaItems.RemoveRange(postMediaItems);
            _context.Posts.RemoveRange(posts);
            _context.ConnectedPages.RemoveRange(pages);
            _context.ConnectedInstagramAccounts.RemoveRange(igs);
            _context.MetaConnections.RemoveRange(connections);
            _context.MetaOAuthStates.RemoveRange(oauthStates);
            _context.Media.RemoveRange(media);
            _context.AiVoiceProfiles.RemoveRange(voiceProfiles);
            _context.SupportContactRequests.RemoveRange(supportRequests);
            _context.WorkspaceMembers.RemoveRange(memberships);
            _context.Workspaces.RemoveRange(workspaces);
            _context.AppUsers.Remove(user);

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

        // ── Storage objects (external, best-effort, AFTER commit) ────────────────
        var storageResult = await _storageDeletion.DeleteObjectsBestEffortAsync(
            storageKeys, allowedPrefixes, ct);

        _logger.LogInformation(
            "Account deletion completed for user {UserId}: workspaces={Workspaces}, posts={Posts}, " +
            "connections={Connections}, media={Media}, storageDeleted={Storage}.",
            authenticatedUserId, workspaces.Count, posts.Count, connections.Count,
            media.Count, storageResult.Deleted);
    }

    private async Task CancelPendingSchedulesAsync(List<Post> posts, CancellationToken ct)
    {
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
                _logger.LogWarning(ex,
                    "CancelScheduleAsync failed for post {PostId} during account deletion", post.Id);
            }
        }
    }

    private static List<string?> CollectStorageKeys(
        List<Entities.Media> media, List<Post> posts, List<PostMediaItem> postMediaItems)
    {
        var keys = new List<string?>();
        foreach (var m in media)
        {
            keys.Add(m.StorageKey);
            keys.Add(m.InstagramImageStorageKey);
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
}
