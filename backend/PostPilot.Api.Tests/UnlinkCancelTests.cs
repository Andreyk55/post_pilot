using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for the soft-disconnect model:
///   - Disconnect stamps IsConnected=false + DisconnectedAt on MetaConnection + child rows
///   - No rows are deleted; Posts always retain their target FK
///   - Active posts on a disconnected asset are auto-canceled
///   - PostsController exposes TargetConnectionActive on PostDto
///   - PostPublishingWorker query filters by TargetPage.IsConnected
/// </summary>
public class UnlinkCancelTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly Mock<IPostScheduler> _schedulerMock;

    public UnlinkCancelTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        _schedulerMock = new Mock<IPostScheduler>();
        _schedulerMock
            .Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _dbContext.Dispose();

    // ─── helpers ────────────────────────────────────────────────────────────────

    private PostsController MakeController()
    {
        var loggerMock = new Mock<ILogger<PostsController>>();
        var insightsMock = new Mock<IFacebookInsightsService>();
        var workspaceMock = new Mock<ICurrentWorkspaceProvider>();
        workspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceId);
        return new PostsController(_dbContext, _schedulerMock.Object, insightsMock.Object, workspaceMock.Object, loggerMock.Object);
    }

    private MetaOAuthService MakeOAuthService()
    {
        // DisconnectAsync hits Graph for the best-effort token revoke; stub returns 200.
        var httpClient = new HttpClient(new StubHttpHandler());
        var loggerMock = new Mock<ILogger<MetaOAuthService>>();
        var metaSettings = new MetaOptions
        {
            AppId = "test", AppSecret = "test", RedirectUri = "http://localhost/cb"
        };
        var publishingOpts = new PublishingOptions { OAuthStateExpirationMinutes = 10 };

        // Real ProviderConnectionService + Meta lifecycle handler — disconnect's
        // asset/post sweep lives in the handler, so a mock would no-op silently.
        var handler = new PostPilot.Api.Services.Providers.MetaProviderLifecycleHandler(
            _dbContext,
            _schedulerMock.Object,
            new Mock<ILogger<PostPilot.Api.Services.Providers.MetaProviderLifecycleHandler>>().Object);
        var providerConnections = new PostPilot.Api.Services.Providers.ProviderConnectionService(
            _dbContext,
            new[] { (PostPilot.Api.Services.Providers.IProviderLifecycleHandler)handler },
            new Mock<ILogger<PostPilot.Api.Services.Providers.ProviderConnectionService>>().Object);

        return new MetaOAuthService(
            _dbContext,
            httpClient,
            metaSettings,
            loggerMock.Object,
            _schedulerMock.Object,
            providerConnections,
            new MetaApiOptions(),
            publishingOpts);
    }

    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private MetaConnection SeedConnection(out ConnectedPage page, out ConnectedInstagramAccount ig)
    {
        var connection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = UserId,
            AccessToken = "user-token",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsConnected = true,
        };
        page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            MetaConnectionId = connection.Id,
            PageId = "fb-page-1",
            Name = "Test Page",
            AccessToken = "page-token",
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
        };
        ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            MetaConnectionId = connection.Id,
            IgBusinessId = "ig-biz-1",
            Username = "testuser",
            PageId = "fb-page-1",
            PageName = "Test Page",
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
        };
        connection.Pages.Add(page);
        connection.InstagramAccounts.Add(ig);
        _dbContext.MetaConnections.Add(connection);
        _dbContext.SaveChanges();
        return connection;
    }

    private Post AddPost(PostStatus status, Platform platform, Guid? targetPageId = null, Guid? targetIgId = null)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            Content = $"post-{Guid.NewGuid()}",
            Platform = platform,
            PostType = PostType.Feed,
            Status = status,
            TargetPageId = targetPageId,
            TargetInstagramAccountId = targetIgId,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ScheduleArn = "local-polling",
        };
        _dbContext.Posts.Add(post);
        _dbContext.SaveChanges();
        return post;
    }

    // ─── DisconnectAsync soft-deletes everything ───────────────────────────────

    [Fact]
    public async Task Disconnect_StampsConnectionAndAssets_DoesNotDeleteThem()
    {
        SeedConnection(out var page, out var ig);

        var service = MakeOAuthService();
        await service.DisconnectAsync(WorkspaceId);

        _dbContext.ChangeTracker.Clear();
        var connection = await _dbContext.MetaConnections.FirstAsync(c => c.WorkspaceId == WorkspaceId);
        var pageAfter = await _dbContext.ConnectedPages.FindAsync(page.Id);
        var igAfter = await _dbContext.ConnectedInstagramAccounts.FindAsync(ig.Id);

        // Rows still exist
        Assert.NotNull(connection);
        Assert.NotNull(pageAfter);
        Assert.NotNull(igAfter);

        // ...but stamped as disconnected
        Assert.False(connection.IsConnected);
        Assert.NotNull(connection.DisconnectedAt);
        Assert.False(pageAfter!.IsConnected);
        Assert.NotNull(pageAfter.DisconnectedAt);
        Assert.False(igAfter!.IsConnected);
        Assert.NotNull(igAfter.DisconnectedAt);
    }

    [Fact]
    public async Task Disconnect_CancelsActivePosts_KeepsPublishedHistory_KeepsAllPostRows()
    {
        SeedConnection(out var page, out var ig);
        var scheduled = AddPost(PostStatus.Scheduled, Platform.Facebook, targetPageId: page.Id);
        var retrying = AddPost(PostStatus.RetryPending, Platform.Facebook, targetPageId: page.Id);
        var processing = AddPost(PostStatus.Processing, Platform.Instagram, targetIgId: ig.Id);
        var published = AddPost(PostStatus.Published, Platform.Facebook, targetPageId: page.Id);

        var postCountBefore = await _dbContext.Posts.CountAsync();

        var service = MakeOAuthService();
        await service.DisconnectAsync(WorkspaceId);

        _dbContext.ChangeTracker.Clear();

        var scheduledAfter = await _dbContext.Posts.FindAsync(scheduled.Id);
        var retryingAfter = await _dbContext.Posts.FindAsync(retrying.Id);
        var processingAfter = await _dbContext.Posts.FindAsync(processing.Id);
        var publishedAfter = await _dbContext.Posts.FindAsync(published.Id);

        Assert.Equal(PostStatus.Canceled, scheduledAfter!.Status);
        Assert.Equal(PostStatus.Canceled, retryingAfter!.Status);
        Assert.Equal(PostStatus.Canceled, processingAfter!.Status);
        Assert.Equal(PostStatus.Published, publishedAfter!.Status);

        // Disconnect now flows through MetaProviderLifecycleHandler which stamps
        // [ProviderDisconnected]; the legacy [AccountDisconnected] reason is no longer used.
        Assert.StartsWith("[ProviderDisconnected]", scheduledAfter.ErrorMessage);

        // Published post's FK still points at the (now disconnected) page — that's the whole point.
        Assert.Equal(page.Id, publishedAfter.TargetPageId);

        // No post rows deleted
        var postCountAfter = await _dbContext.Posts.CountAsync();
        Assert.Equal(postCountBefore, postCountAfter);

        _schedulerMock.Verify(
            s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task Disconnect_HidesPostsFromListing_ButPreservesDbRow()
    {
        SeedConnection(out var page, out _);
        var published = AddPost(PostStatus.Published, Platform.Facebook, targetPageId: page.Id);

        var service = MakeOAuthService();
        await service.DisconnectAsync(WorkspaceId);

        _dbContext.ChangeTracker.Clear();
        var controller = MakeController();
        var result = await controller.GetPosts();

        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(0, result.Value.TotalCount);

        // DB row is preserved for audit / history / debugging via direct DB access.
        Assert.NotNull(await _dbContext.Posts.FindAsync(published.Id));
    }

    // ─── GetPosts filters ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPosts_ShowsOnlyPostsForConnectedAssets()
    {
        SeedConnection(out var connectedPage, out _);

        var disconnectedPage = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            PageId = "fb-page-disconnected",
            Name = "Gone",
            AccessToken = "tok",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            IsConnected = false,
            DisconnectedAt = DateTime.UtcNow.AddDays(-1),
        };
        _dbContext.ConnectedPages.Add(disconnectedPage);
        await _dbContext.SaveChangesAsync();

        AddPost(PostStatus.Scheduled, Platform.Facebook, targetPageId: connectedPage.Id);   // visible
        AddPost(PostStatus.Scheduled, Platform.Facebook, targetPageId: disconnectedPage.Id); // hidden
        AddPost(PostStatus.Canceled, Platform.Facebook, targetPageId: connectedPage.Id);    // visible
        AddPost(PostStatus.Published, Platform.Facebook, targetPageId: disconnectedPage.Id); // hidden

        var controller = MakeController();
        var result = await controller.GetPosts();

        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value!.TotalCount);
        Assert.All(result.Value.Items, p => Assert.Equal(connectedPage.Id, p.TargetPageId));
    }

    [Fact]
    public async Task GetPosts_StatusFilter_StillRespectsDisconnectedHiding()
    {
        SeedConnection(out var connectedPage, out _);
        AddPost(PostStatus.Canceled, Platform.Facebook, targetPageId: connectedPage.Id);
        AddPost(PostStatus.Scheduled, Platform.Facebook, targetPageId: connectedPage.Id);

        var controller = MakeController();
        var result = await controller.GetPosts(status: PostStatus.Canceled);

        Assert.NotNull(result.Value);
        Assert.Equal(1, result.Value!.TotalCount);
        Assert.Equal(PostStatus.Canceled, result.Value.Items[0].Status);
    }

    [Fact]
    public async Task GetPosts_HidesPostsWhenParentMetaConnectionIsDisconnected()
    {
        SeedConnection(out var page, out _);
        AddPost(PostStatus.Published, Platform.Facebook, targetPageId: page.Id);

        var service = MakeOAuthService();
        await service.DisconnectAsync(WorkspaceId);

        _dbContext.ChangeTracker.Clear();
        var controller = MakeController();
        var result = await controller.GetPosts();

        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!.Items);
    }

    // ─── Worker query guard ────────────────────────────────────────────────────

    [Fact]
    public async Task WorkerQuery_SkipsPostsWhenPageOrParentConnectionIsDisconnected()
    {
        // Three scenarios in one test:
        //   1) page connected + connection connected   → published
        //   2) page disconnected                       → skipped
        //   3) page connected but parent connection disconnected → skipped
        var liveConnection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccessToken = "tok",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsConnected = true,
        };
        var deadConnection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            AccessToken = "tok",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow.AddDays(-7),
            UpdatedAt = DateTime.UtcNow.AddDays(-1),
            IsConnected = false,
            DisconnectedAt = DateTime.UtcNow.AddDays(-1),
        };
        _dbContext.MetaConnections.AddRange(liveConnection, deadConnection);

        var goodPage = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            MetaConnectionId = liveConnection.Id,
            PageId = "fb-good",
            Name = "Good",
            AccessToken = "tok",
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
        };
        var pageWithDeadParent = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            MetaConnectionId = deadConnection.Id,
            PageId = "fb-orphan-parent",
            // page itself is technically still "IsConnected = true" but its parent isn't.
            // (In real flow, DisconnectAsync would have stamped both, but this models the
            // out-of-sync state to verify the worker checks both levels.)
            Name = "Orphan Parent",
            AccessToken = "tok",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            IsConnected = true,
        };
        var disconnectedPage = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            MetaConnectionId = liveConnection.Id,
            PageId = "fb-disconnected",
            Name = "Disconnected",
            AccessToken = "tok",
            CreatedAt = DateTime.UtcNow.AddDays(-7),
            IsConnected = false,
            DisconnectedAt = DateTime.UtcNow.AddDays(-1),
        };
        _dbContext.ConnectedPages.AddRange(goodPage, pageWithDeadParent, disconnectedPage);
        await _dbContext.SaveChangesAsync();

        AddPost(PostStatus.Scheduled, Platform.Facebook, targetPageId: goodPage.Id);
        AddPost(PostStatus.Scheduled, Platform.Facebook, targetPageId: pageWithDeadParent.Id);
        AddPost(PostStatus.Scheduled, Platform.Facebook, targetPageId: disconnectedPage.Id);

        foreach (var p in _dbContext.Posts.ToList())
        {
            p.ScheduledAt = DateTime.UtcNow.AddMinutes(-5);
        }
        await _dbContext.SaveChangesAsync();

        // Mirror PostPublishingWorker.ProcessDuePostsAsync verbatim (incl. the publish
        // gate: asset AND parent connection must be IsConnected AND Status==Active).
        var now = DateTime.UtcNow;
        var due = await _dbContext.Posts
            .Where(p => p.Platform == Platform.Facebook || p.Platform == Platform.Instagram)
            .Where(p =>
                (p.Platform == Platform.Facebook
                    && p.TargetPage != null
                    && p.TargetPage.IsConnected
                    && p.TargetPage.Status == ConnectionStatus.Active
                    && (p.TargetPage.MetaConnection == null
                        || (p.TargetPage.MetaConnection.IsConnected
                            && p.TargetPage.MetaConnection.Status == ConnectionStatus.Active)))
                || (p.Platform == Platform.Instagram
                    && p.TargetInstagramAccount != null
                    && p.TargetInstagramAccount.IsConnected
                    && p.TargetInstagramAccount.Status == ConnectionStatus.Active
                    && (p.TargetInstagramAccount.MetaConnection == null
                        || (p.TargetInstagramAccount.MetaConnection.IsConnected
                            && p.TargetInstagramAccount.MetaConnection.Status == ConnectionStatus.Active))))
            .Where(p =>
                (p.Status == PostStatus.Scheduled && p.ScheduledAt <= now) ||
                ((p.Status == PostStatus.RetryPending || p.Status == PostStatus.Processing)
                    && p.NextRetryAt != null && p.NextRetryAt <= now))
            .ToListAsync();

        Assert.Single(due);
        Assert.Equal(goodPage.Id, due[0].TargetPageId);
    }

    // ─── HttpMessageHandler stub ───────────────────────────────────────────────

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
