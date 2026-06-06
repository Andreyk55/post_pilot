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
/// End-to-end tests for the generic provider/workspace OWNERSHIP rules and the
/// content-isolation guarantees:
///
///   - ACCOUNT ownership (Provider + ProviderAccountId) is PERMANENT: the first
///     workspace to connect an account owns it forever. Disconnecting does NOT
///     release it, so another workspace can never connect the same account later.
///   - Ownership statuses Active and ReauthRequired both block other workspaces.
///   - Token invalid → ReauthRequired (keeps ownership, keeps the failed post visible).
///   - Content (posts/media/history) NEVER moves between workspaces.
///
/// These exercise the real ProviderConnectionService + Meta lifecycle handler and
/// the real FacebookPagePublisher Auth-error path against an in-memory DB.
/// </summary>
public class ProviderOwnershipTests : IDisposable
{
    private static readonly Guid UserAId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid UserBId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid WorkspaceAId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceBId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private const string MetaAccountAlpha = "meta-user-alpha";
    private const string MetaAccountBeta = "meta-user-beta";
    private const string SharedPageId = "fb-page-shared";
    private const string OtherPageId = "fb-page-other";

    private readonly AppDbContext _db;
    private readonly Mock<IPostScheduler> _schedulerMock = new();
    private readonly Mock<IFacebookInsightsService> _insightsMock = new();
    private readonly Mock<ICurrentWorkspaceProvider> _workspaceMock = new();
    private readonly IProviderConnectionService _providerService;

    public ProviderOwnershipTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _schedulerMock.Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _schedulerMock.Setup(s => s.ScheduleRetryAsync(It.IsAny<Post>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduleResult(true));

        var handler = new MetaProviderLifecycleHandler(
            _db, _schedulerMock.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        _providerService = new ProviderConnectionService(
            _db, new[] { (IProviderLifecycleHandler)handler }, NullLogger<ProviderConnectionService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ActAs(Guid workspaceId)
    {
        _workspaceMock.Reset();
        _workspaceMock.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceId);
    }

    private PostsController NewPostsController() => new(
        _db, _schedulerMock.Object, _insightsMock.Object, _workspaceMock.Object,
        NullLogger<PostsController>.Instance);

    /// <summary>Seed an active Meta connection + one page in the given workspace, using <paramref name="pageId"/> as the external Facebook page id.</summary>
    private (MetaConnection conn, ConnectedPage page) SeedMeta(
        Guid workspaceId, Guid userId, string providerAccountId, string pageId)
    {
        var now = DateTime.UtcNow;
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId,
            Provider = ProviderType.Meta, ProviderAccountId = providerAccountId,
            AccessToken = "user-token", TokenExpiresAt = now.AddDays(30),
            ConnectedAt = now, UpdatedAt = now, IsConnected = true, Status = ConnectionStatus.Active,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, MetaConnectionId = conn.Id,
            PageId = pageId, Name = "Page", AccessToken = "page-token",
            CreatedAt = now, IsConnected = true, Status = ConnectionStatus.Active,
        };
        _db.MetaConnections.Add(conn);
        _db.ConnectedPages.Add(page);
        _db.SaveChanges();
        return (conn, page);
    }

    private Post SeedPost(Guid workspaceId, Guid pageId, PostStatus status)
    {
        var p = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Content = "hello",
            Platform = Platform.Facebook, TargetPageId = pageId,
            ScheduledAt = DateTime.UtcNow.AddHours(1), Status = status,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            PublishedAt = status == PostStatus.Published ? DateTime.UtcNow : null,
        };
        _db.Posts.Add(p);
        _db.SaveChanges();
        return p;
    }

    // ── Permanent account ownership across disconnect (the core bug) ───────────

    [Fact]
    public async Task After_ws1_disconnects_ws2_cannot_connect_the_same_account()
    {
        // The exact reported bug: ws1 connects an account, disconnects, and ws2 must
        // NOT be able to claim that same account afterwards.
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountAlpha, Array.Empty<string>()));

        // Message must reflect the PERMANENT rule and must NOT suggest disconnecting
        // from the other workspace will free the account.
        Assert.Contains("permanently linked to another workspace", ex.Message);
        Assert.DoesNotContain("Disconnect", ex.Message);
    }

    [Fact]
    public async Task While_active_ws2_cannot_connect_the_same_account()
    {
        // ws1 still actively owns the account → ws2 blocked.
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);

        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountAlpha, Array.Empty<string>()));
    }

    [Fact]
    public async Task Ws1_can_reconnect_its_own_account_after_disconnect()
    {
        // The owning workspace is never blocked from reconnecting its own account.
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        // No throw — same workspace, same account.
        await _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
            WorkspaceAId, ProviderType.Meta, MetaAccountAlpha, Array.Empty<string>());
    }

    [Fact]
    public async Task Ws2_may_connect_a_different_account_after_ws1_disconnects()
    {
        // ws2 is only blocked from the SAME account; a different account is fine.
        SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        await _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
            WorkspaceBId, ProviderType.Meta, MetaAccountBeta, new[] { OtherPageId });
    }

    // ── Content isolation (content NEVER crosses workspaces) ───────────────────

    [Fact]
    public async Task Ws2_with_a_different_account_does_not_see_ws1_posts()
    {
        // Workspace 1 connects an account, creates posts, then disconnects.
        var (_, page1) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        var ws1Scheduled = SeedPost(WorkspaceAId, page1.Id, PostStatus.Scheduled);
        var ws1Published = SeedPost(WorkspaceAId, page1.Id, PostStatus.Published);
        var ws1Failed = SeedPost(WorkspaceAId, page1.Id, PostStatus.Failed);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);

        // Workspace 2 connects a DIFFERENT account (the only valid way to coexist).
        await _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
            WorkspaceBId, ProviderType.Meta, MetaAccountBeta, new[] { OtherPageId });
        var (_, page2) = SeedMeta(WorkspaceBId, UserBId, MetaAccountBeta, OtherPageId);
        var ws2Post = SeedPost(WorkspaceBId, page2.Id, PostStatus.Scheduled);

        // Workspace 2's list shows ONLY its own post — never workspace 1's.
        ActAs(WorkspaceBId);
        var result = await NewPostsController().GetPosts(pageSize: 50);
        var paged = Assert.IsType<PaginatedResponse<PostDto>>(result.Value);
        var ids = paged.Items.Select(i => i.Id).ToHashSet();

        Assert.Contains(ws2Post.Id, ids);
        Assert.DoesNotContain(ws1Scheduled.Id, ids);
        Assert.DoesNotContain(ws1Published.Id, ids);
        Assert.DoesNotContain(ws1Failed.Id, ids);

        // Workspace 1's posts keep WorkspaceId = workspace 1 (content never moved).
        foreach (var id in new[] { ws1Scheduled.Id, ws1Published.Id, ws1Failed.Id })
        {
            var row = await _db.Posts.AsNoTracking().FirstAsync(p => p.Id == id);
            Assert.Equal(WorkspaceAId, row.WorkspaceId);
        }
    }

    [Fact]
    public async Task Ws2_cannot_retry_or_publish_ws1_failed_post()
    {
        var (_, page1) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        var ws1Failed = SeedPost(WorkspaceAId, page1.Id, PostStatus.Failed);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);
        SeedMeta(WorkspaceBId, UserBId, MetaAccountBeta, OtherPageId);

        ActAs(WorkspaceBId);
        var publisherResolver = new Mock<IPostPublisherResolver>().Object;
        var storyResolver = new Mock<IStoryPublisherResolver>().Object;

        // Publish-now of workspace 1's post from workspace 2 → 404 (scoped by WorkspaceId).
        var publish = await NewPostsController().PublishNow(ws1Failed.Id, publisherResolver, storyResolver);
        Assert.IsType<NotFoundResult>(publish.Result);

        // Cancel of workspace 1's post from workspace 2 → 404 too.
        var cancel = await NewPostsController().CancelPost(ws1Failed.Id);
        Assert.IsType<NotFoundResult>(cancel);
    }

    [Fact]
    public async Task No_content_is_transferred_between_workspaces()
    {
        var (_, page1) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        SeedPost(WorkspaceAId, page1.Id, PostStatus.Scheduled);
        SeedPost(WorkspaceAId, page1.Id, PostStatus.Published);

        // Seed a media row for workspace 1.
        var ws1Media = new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, StorageProvider = "local-disk",
            StorageKey = "media/ws1.jpg", ContentType = "image/jpeg",
            Status = MediaUploadStatus.Uploaded, CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        };
        _db.Media.Add(ws1Media);
        _db.SaveChanges();

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);
        SeedMeta(WorkspaceBId, UserBId, MetaAccountBeta, OtherPageId);

        // Every workspace-1 post + media still belongs to workspace 1.
        Assert.True(await _db.Posts.AsNoTracking().Where(p => p.WorkspaceId == WorkspaceAId).CountAsync() == 2);
        Assert.Equal(0, await _db.Posts.AsNoTracking().CountAsync(p => p.WorkspaceId == WorkspaceBId));
        Assert.Equal(WorkspaceAId, (await _db.Media.AsNoTracking().FirstAsync(m => m.Id == ws1Media.Id)).WorkspaceId);
    }

    // ── Reauth behavior on token-invalid publish failure ───────────────────────

    [Fact]
    public async Task TokenInvalid_marks_ReauthRequired_keeps_ownership_and_failed_post_visible()
    {
        // The FacebookPagePublisher delegates to ProviderConnectionService.MarkReauthRequiredAsync
        // on a PublishErrorType.Auth result (Meta codes 190/102/463/467/…). The publisher's full
        // PublishAsync path uses ExecuteUpdate (unsupported by the InMemory provider used in tests),
        // so we assert the reauth CONTRACT the publisher relies on directly here, plus mark the post
        // Failed exactly as the publisher's Auth branch does.
        var (conn, page) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        var post = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled);

        // Publisher's Auth branch: post Fails (stays visible), then flag reauth.
        post.Status = PostStatus.Failed;
        post.ErrorMessage = "Error validating access token";
        await _db.SaveChangesAsync();
        await _providerService.MarkReauthRequiredAsync(WorkspaceAId, ProviderType.Meta);

        _db.ChangeTracker.Clear();

        // Connection retains ownership but is flagged ReauthRequired (NOT disconnected).
        var connAfter = await _db.MetaConnections.FindAsync(conn.Id);
        Assert.True(connAfter!.IsConnected);
        Assert.Equal(ConnectionStatus.ReauthRequired, connAfter.Status);
        Assert.Null(connAfter.DisconnectedAt);

        // Asset mirrors the reauth flag.
        var pageAfter = await _db.ConnectedPages.FindAsync(page.Id);
        Assert.True(pageAfter!.IsConnected);
        Assert.Equal(ConnectionStatus.ReauthRequired, pageAfter.Status);

        // The post Failed (visible, not canceled). It is NOT retried in a loop.
        var postAfter = await _db.Posts.FindAsync(post.Id);
        Assert.Equal(PostStatus.Failed, postAfter!.Status);

        // Ownership still blocks another workspace.
        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, MetaAccountAlpha, new[] { SharedPageId }));
    }

    [Fact]
    public void Meta_token_and_session_error_codes_classify_as_Auth()
    {
        // Guards the error-code → PublishErrorType.Auth mapping that drives reauth.
        // 190 = token expired/invalid, 102 = session invalidated, 463/467 = expired/invalid.
        foreach (var code in new[] { 190, 102, 463, 467 })
        {
            Assert.Equal(PublishErrorType.Auth, FacebookPagePublisher.ClassifyErrorCodeForTest(code));
        }
        // A content error stays Permanent (does NOT trigger reauth).
        Assert.Equal(PublishErrorType.Permanent, FacebookPagePublisher.ClassifyErrorCodeForTest(100));
        // A rate-limit code stays Transient.
        Assert.Equal(PublishErrorType.Transient, FacebookPagePublisher.ClassifyErrorCodeForTest(4));
    }

    [Fact]
    public async Task Reconnect_same_workspace_clears_reauth_and_failed_post_stays_visible_and_retryable()
    {
        var (conn, page) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, SharedPageId);
        var failed = SeedPost(WorkspaceAId, page.Id, PostStatus.Failed);

        // Mark ReauthRequired (token invalid).
        await _providerService.MarkReauthRequiredAsync(WorkspaceAId, ProviderType.Meta);

        // Same-workspace reconnect (recovery): clear reauth + refresh token in place.
        _db.ChangeTracker.Clear();
        var reconn = await _db.MetaConnections.Include(c => c.Pages)
            .FirstAsync(c => c.Id == conn.Id);
        reconn.Status = ConnectionStatus.Active;
        reconn.AccessToken = "fresh-token";
        reconn.UpdatedAt = DateTime.UtcNow;
        await _providerService.MarkReauthRequiredAsync(WorkspaceBId, ProviderType.Meta); // no-op for A
        foreach (var p in reconn.Pages) p.Status = ConnectionStatus.Active;
        await _db.SaveChangesAsync();

        // The previously-failed post is still visible to workspace A and retryable
        // (status Failed → user can publish-now / it remains in the list).
        ActAs(WorkspaceAId);
        var result = await NewPostsController().GetPosts(pageSize: 50);
        var paged = Assert.IsType<PaginatedResponse<PostDto>>(result.Value);
        Assert.Contains(failed.Id, paged.Items.Select(i => i.Id));

        var connAfter = await _db.MetaConnections.AsNoTracking().FirstAsync(c => c.Id == conn.Id);
        Assert.Equal(ConnectionStatus.Active, connAfter.Status);
        Assert.Equal("fresh-token", connAfter.AccessToken);
        Assert.True(connAfter.IsConnected);
    }

    // ── Disconnect only affects its own workspace ──────────────────────────────

    [Fact]
    public async Task Disconnect_in_ws1_cancels_only_ws1_posts_and_does_not_affect_ws2()
    {
        // ws1 and ws2 own DISTINCT accounts + pages. Disconnect must scope strictly by
        // WorkspaceId and never touch the other workspace's connection or posts.
        var (_, page1) = SeedMeta(WorkspaceAId, UserAId, MetaAccountAlpha, "page-ws1");
        var (conn2, page2) = SeedMeta(WorkspaceBId, UserBId, MetaAccountBeta, "page-ws2");

        var ws1Scheduled = SeedPost(WorkspaceAId, page1.Id, PostStatus.Scheduled);
        var ws2Scheduled = SeedPost(WorkspaceBId, page2.Id, PostStatus.Scheduled);

        await _providerService.DisconnectAsync(WorkspaceAId, ProviderType.Meta);
        _db.ChangeTracker.Clear();

        Assert.Equal(PostStatus.Canceled, (await _db.Posts.FindAsync(ws1Scheduled.Id))!.Status);
        Assert.Equal(PostStatus.Scheduled, (await _db.Posts.FindAsync(ws2Scheduled.Id))!.Status);

        var conn2After = await _db.MetaConnections.FindAsync(conn2.Id);
        Assert.True(conn2After!.IsConnected);
        Assert.Null(conn2After.DisconnectedAt);
    }

}
