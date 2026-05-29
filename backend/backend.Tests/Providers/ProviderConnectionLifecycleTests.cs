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

    // ── B. Workspace isolation ───────────────────────────────────────────────

    [Fact]
    public async Task Same_provider_account_can_be_connected_to_two_workspaces_independently()
    {
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        SeedMeta(WorkspaceBId, UserBId, MetaAccountAlpha);

        var inA = await _providerService.GetActiveConnectionAsync(WorkspaceAId, ProviderType.Meta);
        var inB = await _providerService.GetActiveConnectionAsync(WorkspaceBId, ProviderType.Meta);

        Assert.NotNull(inA);
        Assert.NotNull(inB);
        Assert.NotEqual(inA!.ConnectionId, inB!.ConnectionId);

        // Disconnect in A leaves B untouched.
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        Assert.Null(await _providerService.GetActiveConnectionAsync(WorkspaceAId, ProviderType.Meta));
        Assert.NotNull(await _providerService.GetActiveConnectionAsync(WorkspaceBId, ProviderType.Meta));
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
        var (_, aPage, _) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        var (_, bPage, _) = SeedMeta(WorkspaceBId, UserBId, MetaAccountAlpha);

        var aScheduled = SeedPost(WorkspaceAId, aPage.Id, PostStatus.Scheduled);
        var bScheduled = SeedPost(WorkspaceBId, bPage.Id, PostStatus.Scheduled);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        await _db.Entry(aScheduled).ReloadAsync();
        await _db.Entry(bScheduled).ReloadAsync();
        Assert.Equal(PostStatus.Canceled, aScheduled.Status);
        Assert.Equal(PostStatus.Scheduled, bScheduled.Status);
    }
}
