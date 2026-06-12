using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests.Providers;

/// <summary>
/// Provider connect/disconnect/reconnect lifecycle tests, driven through the
/// generic <see cref="ProviderConnectionService"/> + Meta lifecycle handler.
///
/// These pin down the MVP product rules:
///   A. One active connection per (workspace, provider) — duplicate connect rejected.
///   B. Workspaces are isolated — same Meta account in two workspaces is fine.
///   C. Disconnect cancels non-executed posts; executed history untouched.
///   D. While disconnected, normal post list hides every Meta-tied post.
///   E. Reconnect SAME ProviderAccountId resurfaces history (Published + Canceled).
///   F. Connect DIFFERENT ProviderAccountId leaves old history hidden.
///   G. Cross-workspace operations cannot reach another workspace's connection.
///
/// The tests bypass MetaOAuthService's HTTP layer and operate directly on
/// the generic lifecycle. Reconnecting "the same account" is modeled by
/// flipping the existing disconnected MetaConnection row back to IsConnected=true
/// — same path the OAuth service takes after resolving the same ProviderAccountId.
/// </summary>
public class ProviderConnectionLifecycleTests : IDisposable
{
    private static readonly Guid UserAId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid UserBId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid WorkspaceAId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceBId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private const string MetaAccountAlpha = "meta-user-alpha";
    private const string MetaAccountBeta = "meta-user-beta";

    private readonly AppDbContext _db;
    private readonly Mock<ICurrentUserProvider> _userMock = new();
    private readonly Mock<ICurrentWorkspaceProvider> _workspaceMock = new();
    private readonly Mock<IPostScheduler> _schedulerMock = new();
    private readonly Mock<IFacebookInsightsService> _insightsMock = new();

    private readonly IProviderConnectionService _providerService;

    public ProviderConnectionLifecycleTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        SeedTwoWorkspaces();
        ActAs(UserAId, WorkspaceAId);

        _schedulerMock.Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var metaHandler = new MetaProviderLifecycleHandler(
            _db, _schedulerMock.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        _providerService = new ProviderConnectionService(
            _db, new[] { (IProviderLifecycleHandler)metaHandler }, NullLogger<ProviderConnectionService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ActAs(Guid userId, Guid workspaceId)
    {
        _userMock.Reset();
        _workspaceMock.Reset();
        _userMock.Setup(u => u.GetCurrentUserId()).Returns(userId);
        _userMock.Setup(u => u.TryGetCurrentUserId(out userId)).Returns(true);
        _workspaceMock.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceId);
        _workspaceMock.Setup(w => w.GetCurrentWorkspaceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentWorkspaceInfo(userId, workspaceId, "Test"));
    }

    private PostsController NewPostsController() => new(
        _db,
        Mock.Of<IPostScheduler>(),
        _insightsMock.Object,
        _workspaceMock.Object,
        new PassThroughMediaGate(),
        NullLogger<PostsController>.Instance);

    private void SeedTwoWorkspaces()
    {
        var now = DateTime.UtcNow;
        _db.AppUsers.AddRange(
            new AppUser
            {
                Id = UserAId, Email = "a@test", DisplayName = "A",
                AuthProvider = "google", ExternalAuthUserId = "a-sub",
                CurrentWorkspaceId = WorkspaceAId, CreatedAt = now, UpdatedAt = now,
            },
            new AppUser
            {
                Id = UserBId, Email = "b@test", DisplayName = "B",
                AuthProvider = "google", ExternalAuthUserId = "b-sub",
                CurrentWorkspaceId = WorkspaceBId, CreatedAt = now, UpdatedAt = now,
            });

        _db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceAId, Name = "Workspace A", OwnerUserId = UserAId, CreatedAt = now, UpdatedAt = now },
            new Workspace { Id = WorkspaceBId, Name = "Workspace B", OwnerUserId = UserBId, CreatedAt = now, UpdatedAt = now });

        _db.WorkspaceMembers.AddRange(
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, UserId = UserAId, Role = WorkspaceRole.Owner, CreatedAt = now },
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId, UserId = UserBId, Role = WorkspaceRole.Owner, CreatedAt = now });

        _db.SaveChanges();
    }

    /// <summary>
    /// Seed an active Meta connection with one Page and one IG account.
    /// Returns the connection + asset rows so tests can wire posts to them.
    /// </summary>
    private (MetaConnection conn, ConnectedPage page, ConnectedInstagramAccount ig) SeedMeta(
        Guid workspaceId, Guid ownerUserId, string providerAccountId)
    {
        var now = DateTime.UtcNow;
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = ownerUserId,
            Provider = ProviderType.Meta,
            ProviderAccountId = providerAccountId,
            ProviderAccountName = providerAccountId,
            AccessToken = "user-token",
            TokenExpiresAt = now.AddDays(30),
            ConnectedAt = now,
            UpdatedAt = now,
            IsConnected = true,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MetaConnectionId = conn.Id,
            PageId = $"page-{providerAccountId}",
            Name = "Page",
            AccessToken = "page-token",
            CreatedAt = now,
            IsConnected = true,
        };
        var ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MetaConnectionId = conn.Id,
            IgBusinessId = $"ig-{providerAccountId}",
            Username = "user",
            PageId = page.PageId,
            PageName = page.Name,
            CreatedAt = now,
            IsConnected = true,
        };
        _db.MetaConnections.Add(conn);
        _db.ConnectedPages.Add(page);
        _db.ConnectedInstagramAccounts.Add(ig);
        _db.SaveChanges();
        return (conn, page, ig);
    }

    private Post SeedPost(
        Guid workspaceId, Guid targetPageId,
        PostStatus status, Platform platform = Platform.Facebook)
    {
        var p = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Content = "hello",
            Platform = platform,
            TargetPageId = targetPageId,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            PublishedAt = status == PostStatus.Published ? DateTime.UtcNow : null,
        };
        _db.Posts.Add(p);
        _db.SaveChanges();
        return p;
    }

    /// <summary>
    /// Simulates "reconnect the same provider account": find the disconnected
    /// MetaConnection row matching (workspaceId, Provider, ProviderAccountId)
    /// and flip it back to active — same code path MetaOAuthService.CompleteOAuth
    /// takes after FetchMetaUserIdentityAsync resolves the same id.
    /// </summary>
    private async Task<MetaConnection> ReconnectSameAccountAsync(Guid workspaceId, string providerAccountId)
    {
        await _providerService.EnsureCanConnectAsync(workspaceId, ProviderType.Meta);
        var now = DateTime.UtcNow;
        var existing = await _db.MetaConnections
            .Include(c => c.Pages)
            .Include(c => c.InstagramAccounts)
            .FirstOrDefaultAsync(c =>
                c.WorkspaceId == workspaceId
                && c.Provider == ProviderType.Meta
                && c.ProviderAccountId == providerAccountId
                && !c.IsConnected);
        Assert.NotNull(existing);
        existing!.IsConnected = true;
        existing.DisconnectedAt = null;
        existing.UpdatedAt = now;
        existing.ConnectedAt = now;
        // Spec: reactivating must NOT restore individual asset rows automatically.
        // Pages/IGs that were disconnected stay disconnected — but historical posts
        // pointing at them become visible again because the parent connection is
        // back. (In a real reconnect, OAuth's ReconcileSelectedAssetsAsync would
        // re-attach selected pages by external PageId.)
        await _db.SaveChangesAsync();
        return existing;
    }

    // ── A. Uniqueness ────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureCanConnect_throws_when_workspace_already_has_active_meta()
    {
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        var ex = await Assert.ThrowsAsync<ProviderAlreadyConnectedException>(
            () => _providerService.EnsureCanConnectAsync(WorkspaceAId, ProviderType.Meta));
        Assert.Equal(ProviderType.Meta, ex.Provider);
        Assert.Contains("Disconnect it before connecting another one", ex.Message);
    }

    [Fact]
    public async Task EnsureCanConnect_passes_after_disconnect()
    {
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        // Should not throw.
        await _providerService.EnsureCanConnectAsync(WorkspaceAId, ProviderType.Meta);
    }

    // ── B. Cross-workspace exclusive ownership ───────────────────────────────
    //
    // Product rule: a provider account identity may be OWNED by only ONE workspace.
    // ACCOUNT-level ownership (Provider + ProviderAccountId) is PERMANENT — the first
    // workspace to connect an account owns it forever; disconnecting there does NOT
    // release it to another workspace. ASSET-level ownership (page / IG) is held while
    // IsConnected = true (Active OR ReauthRequired); a real Disconnect frees the asset,
    // but the permanent account binding still prevents another workspace from bringing
    // those assets back via the same account.

    [Fact]
    public async Task Different_workspace_connecting_same_account_is_blocked_while_owned()
    {
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        // Workspace B tries to connect the SAME Meta account → blocked.
        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountAlpha, Array.Empty<string>()));
        Assert.Contains("already linked to another workspace", ex.Message);
        // The message must NOT suggest disconnecting elsewhere will free the account.
        Assert.DoesNotContain("Disconnect", ex.Message);
    }

    [Fact]
    public async Task Different_workspace_connecting_same_page_is_blocked_while_owned()
    {
        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        // Workspace B tries to connect a DIFFERENT account but the SAME page asset.
        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountBeta, new[] { page.PageId }));
        Assert.Equal(ProviderType.Meta, ex.Provider);
    }

    [Fact]
    public async Task ReauthRequired_account_still_blocks_another_workspace()
    {
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        // A's connection goes ReauthRequired (token invalid) — ownership retained.
        await _providerService.MarkReauthRequiredAsync(WorkspaceAId, ProviderType.Meta);

        // B is still blocked.
        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountAlpha, Array.Empty<string>()));
    }

    [Fact]
    public async Task Same_workspace_is_not_blocked_by_its_own_ownership()
    {
        var (_, page, ig) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        // Workspace A reconnecting its OWN account/page/IG must NOT be blocked.
        await _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
            WorkspaceAId, ProviderType.Meta, MetaAccountAlpha, new[] { page.PageId, ig.IgBusinessId });
    }

    [Fact]
    public async Task Disconnect_does_NOT_release_account_ownership_to_another_workspace()
    {
        // Account-level ownership is PERMANENT: disconnecting in the owning workspace
        // must NOT let a different workspace claim the same provider account later.
        var (_, page, ig) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        // While A owns it, B is blocked.
        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountAlpha, new[] { page.PageId, ig.IgBusinessId }));

        // A disconnects (real user-initiated). The identity row survives (disconnected).
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        // B STILL cannot connect the same account — ownership is permanent.
        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountAlpha, Array.Empty<string>()));
        Assert.Contains("already linked to another workspace", ex.Message);

        // But B can connect a DIFFERENT account.
        await _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
            WorkspaceBId, ProviderType.Meta, MetaAccountBeta, Array.Empty<string>());
    }

    // ── B2. Permanent workspace+provider→account binding (rule #3) ───────────
    //
    // The FIRST account a workspace connects for a provider pins it permanently.
    // After disconnect, only that same account may reconnect; a different account
    // is rejected — even though the prior row is disconnected (ownership released).

    [Fact]
    public async Task Connecting_different_account_after_disconnect_is_rejected()
    {
        // Workspace A connects account Alpha, then disconnects (frees the active slot).
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        // The active-connection guard now PASSES (nothing active) ...
        await _providerService.EnsureCanConnectAsync(WorkspaceAId, ProviderType.Meta);

        // ... but the permanent binding rejects a DIFFERENT account.
        var ex = await Assert.ThrowsAsync<ProviderAccountMismatchException>(
            () => _providerService.EnsureAccountMatchesWorkspaceBindingAsync(
                WorkspaceAId, ProviderType.Meta, MetaAccountBeta));
        Assert.Equal(ProviderType.Meta, ex.Provider);
        Assert.Equal(MetaAccountAlpha, ex.BoundAccountId);
        Assert.Equal(MetaAccountBeta, ex.AttemptedAccountId);

        // User-facing copy: generic, consistent, and safe.
        Assert.Equal(ProviderAccountMismatchException.UserMessage, ex.Message);
        Assert.Equal(
            "This workspace is already linked to a different provider account. " +
            "Reconnect the original account for this workspace, or use another workspace.",
            ex.Message);
        // Does not suggest disconnecting elsewhere, and never leaks account ids.
        Assert.DoesNotContain("Disconnect", ex.Message);
        Assert.DoesNotContain(MetaAccountAlpha, ex.Message);
        Assert.DoesNotContain(MetaAccountBeta, ex.Message);
    }

    [Fact]
    public async Task Reconnecting_same_account_after_disconnect_is_allowed()
    {
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        // The same account is permitted by the binding guard (no throw).
        await _providerService.EnsureAccountMatchesWorkspaceBindingAsync(
            WorkspaceAId, ProviderType.Meta, MetaAccountAlpha);
    }

    [Fact]
    public async Task Binding_guard_is_noop_when_workspace_has_no_prior_identity()
    {
        // Fresh workspace, no rows yet — any account is allowed (first connect).
        await _providerService.EnsureAccountMatchesWorkspaceBindingAsync(
            WorkspaceAId, ProviderType.Meta, MetaAccountAlpha);
    }

    [Fact]
    public async Task Binding_guard_is_noop_when_incoming_identity_is_unresolved()
    {
        // If Graph /me failed, incomingAccountId is null — we cannot enforce the
        // binding and fall back to the looser guards. Must not throw.
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        await _providerService.EnsureAccountMatchesWorkspaceBindingAsync(
            WorkspaceAId, ProviderType.Meta, null);
        await _providerService.EnsureAccountMatchesWorkspaceBindingAsync(
            WorkspaceAId, ProviderType.Meta, "");
    }

    [Fact]
    public async Task Binding_is_per_workspace_a_different_workspace_can_bind_a_different_account()
    {
        // Workspace A is bound to Alpha (and disconnects). Workspace B has no prior
        // identity, so it may bind Beta — the binding is per-workspace, not global.
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        await _providerService.EnsureAccountMatchesWorkspaceBindingAsync(
            WorkspaceBId, ProviderType.Meta, MetaAccountBeta);
    }

    // ── B3. Disconnect clears stored credentials (rule #6) ───────────────────

    [Fact]
    public async Task Disconnect_clears_stored_connection_and_page_tokens_but_keeps_identity()
    {
        var (conn, page, ig) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        Assert.False(string.IsNullOrEmpty(conn.AccessToken));
        Assert.False(string.IsNullOrEmpty(page.AccessToken));

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);
        _db.ChangeTracker.Clear();

        // Connection token cleared; identity retained.
        var connAfter = await _db.MetaConnections.AsNoTracking().FirstAsync(c => c.Id == conn.Id);
        Assert.Null(connAfter.AccessToken);
        Assert.False(connAfter.IsConnected);
        Assert.NotNull(connAfter.DisconnectedAt);
        Assert.Equal(MetaAccountAlpha, connAfter.ProviderAccountId);
        Assert.Equal(MetaAccountAlpha, connAfter.ProviderAccountName);
        Assert.Equal(ProviderType.Meta, connAfter.Provider);
        Assert.Equal(WorkspaceAId, connAfter.WorkspaceId);

        // Page token cleared; page identity (external PageId) retained.
        var pageAfter = await _db.ConnectedPages.AsNoTracking().FirstAsync(p => p.Id == page.Id);
        Assert.Null(pageAfter.AccessToken);
        Assert.False(pageAfter.IsConnected);
        Assert.Equal(page.PageId, pageAfter.PageId);

        // IG identity retained (it has no own token column).
        var igAfter = await _db.ConnectedInstagramAccounts.AsNoTracking().FirstAsync(i => i.Id == ig.Id);
        Assert.False(igAfter.IsConnected);
        Assert.Equal(ig.IgBusinessId, igAfter.IgBusinessId);
    }

    // ── C. Disconnect cancels non-executed posts; executed untouched ────────

    [Fact]
    public async Task Disconnect_cancels_scheduled_retry_processing_posts_and_leaves_executed_alone()
    {
        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        var scheduled = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled);
        var retry = SeedPost(WorkspaceAId, page.Id, PostStatus.RetryPending);
        var processing = SeedPost(WorkspaceAId, page.Id, PostStatus.Processing);
        var published = SeedPost(WorkspaceAId, page.Id, PostStatus.Published);
        var failed = SeedPost(WorkspaceAId, page.Id, PostStatus.Failed);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        await _db.Entry(scheduled).ReloadAsync();
        await _db.Entry(retry).ReloadAsync();
        await _db.Entry(processing).ReloadAsync();
        await _db.Entry(published).ReloadAsync();
        await _db.Entry(failed).ReloadAsync();

        // Non-executed → Canceled with provider-aware metadata.
        foreach (var p in new[] { scheduled, retry, processing })
        {
            Assert.Equal(PostStatus.Canceled, p.Status);
            Assert.NotNull(p.CanceledAt);
            Assert.Equal(CancellationReason.ProviderDisconnected, p.CancellationReason);
            Assert.Equal(ProviderType.Meta, p.CanceledBecauseProvider);
            Assert.Equal(MetaAccountAlpha, p.CanceledBecauseProviderAccountId);
        }

        // Executed history is unchanged.
        Assert.Equal(PostStatus.Published, published.Status);
        Assert.Equal(PostStatus.Failed, failed.Status);
    }

    // ── D. Visibility while disconnected ─────────────────────────────────────

    [Fact]
    public async Task GetPosts_hides_every_meta_tied_post_while_disconnected()
    {
        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled);
        SeedPost(WorkspaceAId, page.Id, PostStatus.Published);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        var result = await NewPostsController().GetPosts();
        var paged = Assert.IsType<PaginatedResponse<PostDto>>(result.Value);
        Assert.Empty(paged.Items);

        // But the rows still exist in the DB (history preserved).
        Assert.Equal(2, await _db.Posts.CountAsync(p => p.WorkspaceId == WorkspaceAId));
    }

    // ── E. Reconnect SAME account resurfaces history ────────────────────────

    [Fact]
    public async Task Reconnect_same_account_resurfaces_published_and_canceled_history()
    {
        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        var scheduled = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled);
        var published = SeedPost(WorkspaceAId, page.Id, PostStatus.Published);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);
        await ReconnectSameAccountAsync(WorkspaceAId, MetaAccountAlpha);

        var result = await NewPostsController().GetPosts();
        var paged = Assert.IsType<PaginatedResponse<PostDto>>(result.Value);
        var ids = paged.Items.Select(i => i.Id).ToHashSet();

        Assert.Contains(scheduled.Id, ids);  // canceled history, now visible
        Assert.Contains(published.Id, ids);  // published history, now visible

        // Canceled status NOT restored to Scheduled — that would defeat the
        // "canceled posts are permanent history" rule.
        await _db.Entry(scheduled).ReloadAsync();
        Assert.Equal(PostStatus.Canceled, scheduled.Status);
    }

    // ── F. Connect DIFFERENT account keeps old history hidden ───────────────

    [Fact]
    public async Task Connect_different_account_keeps_old_account_history_hidden()
    {
        var (_, alphaPage, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        SeedPost(WorkspaceAId, alphaPage.Id, PostStatus.Scheduled);
        SeedPost(WorkspaceAId, alphaPage.Id, PostStatus.Published);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        // Connect a DIFFERENT provider account. The seed helper creates new
        // assets; alpha's pages remain soft-disconnected.
        var (_, betaPage, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountBeta);
        var betaPost = SeedPost(WorkspaceAId, betaPage.Id, PostStatus.Scheduled);

        var result = await NewPostsController().GetPosts();
        var paged = Assert.IsType<PaginatedResponse<PostDto>>(result.Value);
        var ids = paged.Items.Select(i => i.Id).ToHashSet();

        // Only beta's post is visible. Alpha's history stays hidden.
        Assert.Single(ids);
        Assert.Contains(betaPost.Id, ids);

        // Alpha's canceled scheduled post is still Canceled in DB.
        var canceledCount = await _db.Posts.CountAsync(p =>
            p.WorkspaceId == WorkspaceAId
            && p.CancellationReason == CancellationReason.ProviderDisconnected
            && p.CanceledBecauseProviderAccountId == MetaAccountAlpha);
        Assert.Equal(1, canceledCount);
    }

    // ── G. Cross-workspace isolation of lifecycle ops ────────────────────────

    [Fact]
    public async Task GetActive_for_other_workspace_returns_null()
    {
        SeedMeta(WorkspaceBId, UserBId, MetaAccountBeta);

        // Currently acting as User A in Workspace A. The service is called with
        // workspaceId from the request — users never pass it explicitly, but if
        // someone constructed a request that targeted a workspace they don't
        // belong to, the controllers/auth layer would 401 before reaching here.
        // The provider service itself only sees the workspace id passed in.
        var inA = await _providerService.GetActiveConnectionAsync(WorkspaceAId, ProviderType.Meta);
        Assert.Null(inA);

        // Sanity: B's connection is visible when queried with B's workspace id.
        var inB = await _providerService.GetActiveConnectionAsync(WorkspaceBId, ProviderType.Meta);
        Assert.NotNull(inB);
    }

    [Fact]
    public async Task Disconnect_in_workspace_A_does_not_touch_workspace_B_posts()
    {
        // Each workspace owns a DISTINCT account (account ownership is exclusive +
        // permanent, so the same account can never be active in two workspaces).
        var (_, aPage, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        var (_, bPage, _) = SeedMeta(WorkspaceBId, UserBId, MetaAccountBeta);

        var aScheduled = SeedPost(WorkspaceAId, aPage.Id, PostStatus.Scheduled);
        var bScheduled = SeedPost(WorkspaceBId, bPage.Id, PostStatus.Scheduled);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        await _db.Entry(aScheduled).ReloadAsync();
        await _db.Entry(bScheduled).ReloadAsync();
        Assert.Equal(PostStatus.Canceled, aScheduled.Status);
        Assert.Equal(PostStatus.Scheduled, bScheduled.Status);
    }
}
