using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Regression tests for the production bug where a connected Facebook Page with a linked
/// Instagram professional account showed the IG as "Linked" in discovery but the IG never
/// became a connected publishable asset — leaving "Connected accounts" empty and blocking
/// the Instagram composer with "No Instagram Business Account connected".
///
/// The fix: an IG linked to a CONNECTED page is auto-promoted to a connected
/// <see cref="ConnectedInstagramAccount"/> on every connect / update / refresh, scoped to
/// the page's workspace + Meta connection. These tests drive the real service against a
/// routed fake Graph API so the persisted DB state — the single source of truth every
/// gate reads — is asserted directly.
/// </summary>
public class InstagramAutoPromotionTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid OtherWorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private const string PageId = "fb-page-1";
    private const string PageName = "Posts Dev Page";
    private const string PageToken = "page-token-1";
    private const string IgBusinessId = "ig-biz-appquestor";
    private const string IgUsername = "appquestor";

    private readonly AppDbContext _db;
    private readonly Mock<IPostScheduler> _schedulerMock;

    public InstagramAutoPromotionTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _schedulerMock = new Mock<IPostScheduler>();
        _schedulerMock
            .Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _schedulerMock
            .Setup(s => s.ScheduleAsync(It.IsAny<Post>()))
            .ReturnsAsync(new ScheduleResult(true, "arn", null));
    }

    public void Dispose() => _db.Dispose();

    // ─── service wiring ─────────────────────────────────────────────────────────

    private MetaOAuthService MakeOAuthService()
    {
        var httpClient = new HttpClient(new GraphApiFakeHandler());
        var metaSettings = new MetaOptions { AppId = "test", AppSecret = "test", RedirectUri = "http://localhost/cb" };
        var publishingOpts = new PublishingOptions { OAuthStateExpirationMinutes = 10 };

        var handler = new MetaProviderLifecycleHandler(
            _db, _schedulerMock.Object,
            new Mock<ILogger<MetaProviderLifecycleHandler>>().Object);
        var providerConnections = new ProviderConnectionService(
            _db,
            new IProviderLifecycleHandler[] { handler },
            new Mock<ILogger<ProviderConnectionService>>().Object);

        return new MetaOAuthService(
            _db, httpClient, metaSettings,
            new Mock<ILogger<MetaOAuthService>>().Object,
            _schedulerMock.Object, providerConnections,
            new MetaApiOptions(), publishingOpts);
    }

    private PostsController MakePostsController()
    {
        var workspace = new Mock<ICurrentWorkspaceProvider>();
        workspace.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceId);
        return new PostsController(
            _db, _schedulerMock.Object, Mock.Of<IFacebookInsightsService>(),
            workspace.Object, new PassThroughMediaGate(), NullLogger<PostsController>.Instance);
    }

    // ─── seed helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the exact broken production shape: a CONNECTED Meta connection with a CONNECTED
    /// page, but NO ConnectedInstagramAccount row — even though Graph reports a linked IG for
    /// that page. The page id matches what the fake Graph handler links an IG to.
    /// </summary>
    private MetaConnection SeedConnectedPageWithoutIg(Guid workspaceId)
    {
        var connection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = UserId,
            Provider = ProviderType.Meta,
            ProviderAccountId = "meta-user-1",
            AccessToken = "user-token",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsConnected = true,
            Status = ConnectionStatus.Active,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            MetaConnectionId = connection.Id,
            PageId = PageId,
            Name = PageName,
            AccessToken = PageToken,
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
            Status = ConnectionStatus.Active,
        };
        connection.Pages.Add(page);
        _db.MetaConnections.Add(connection);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return connection;
    }

    // ─── 1. Refresh promotes a linked IG on a connected page ─────────────────────

    [Fact]
    public async Task RefreshAssets_PromotesLinkedIg_ForConnectedPage()
    {
        SeedConnectedPageWithoutIg(WorkspaceId);

        var service = MakeOAuthService();
        await service.RefreshAssetsAsync(WorkspaceId);

        _db.ChangeTracker.Clear();
        var igs = await _db.ConnectedInstagramAccounts
            .Where(i => i.WorkspaceId == WorkspaceId)
            .ToListAsync();

        var ig = Assert.Single(igs);
        Assert.Equal(IgBusinessId, ig.IgBusinessId);
        Assert.Equal(IgUsername, ig.Username);
        Assert.Equal(PageId, ig.PageId);              // keeps the linked FB page for publishing
        Assert.True(ig.IsConnected);                  // connected/publishable, not merely "linked"
        Assert.Equal(ConnectionStatus.Active, ig.Status);
        Assert.False(ig.DisconnectedAt.HasValue);
    }

    [Fact]
    public async Task RefreshAssets_IsIdempotent_NoDuplicateIgRows()
    {
        SeedConnectedPageWithoutIg(WorkspaceId);

        var service = MakeOAuthService();
        await service.RefreshAssetsAsync(WorkspaceId);
        await service.RefreshAssetsAsync(WorkspaceId);

        _db.ChangeTracker.Clear();
        var count = await _db.ConnectedInstagramAccounts.CountAsync(i => i.WorkspaceId == WorkspaceId);
        Assert.Equal(1, count);
    }

    // ─── 2. Update (Assets-page connect path) promotes the linked IG ─────────────

    [Fact]
    public async Task UpdateConnection_PromotesLinkedIg_EvenWhenNotInSelectedInstagramIds()
    {
        SeedConnectedPageWithoutIg(WorkspaceId);

        var service = MakeOAuthService();
        // The Assets page passes the connected page id but an EMPTY IG selection
        // (it only forwards already-connected IGs). The linked IG must still promote.
        await service.UpdateConnectionAsync(
            WorkspaceId,
            selectedPageIds: new List<string> { PageId },
            selectedInstagramIds: new List<string>());

        _db.ChangeTracker.Clear();
        var ig = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);
        Assert.Equal(IgBusinessId, ig.IgBusinessId);
        Assert.True(ig.IsConnected);
    }

    // ─── 3. Post-creation validation passes once the IG is connected ─────────────

    [Fact]
    public async Task CreateInstagramPost_Succeeds_AfterLinkedIgPromoted()
    {
        SeedConnectedPageWithoutIg(WorkspaceId);

        // Before repair: no connected IG ⇒ validation must reject (proves the gate
        // really keys off the persisted ConnectedInstagramAccount row).
        var igBefore = await _db.ConnectedInstagramAccounts.FirstOrDefaultAsync(i => i.WorkspaceId == WorkspaceId);
        Assert.Null(igBefore);

        await MakeOAuthService().RefreshAssetsAsync(WorkspaceId);
        _db.ChangeTracker.Clear();

        var igRow = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);

        var controller = MakePostsController();
        var request = new CreatePostRequest(
            Content: "hello instagram",
            MediaUrl: "https://example.com/pic.jpg",
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            PostType: PostType.Feed,
            TargetInstagramAccountId: igRow.Id);

        var result = await controller.CreatePost(request);

        // Success path returns CreatedAtAction. Had the IG account been missing/disconnected,
        // the controller would have returned 409 Conflict instead (INTEGRATION_DISCONNECTED).
        Assert.IsType<CreatedAtActionResult>(result.Result);

        _db.ChangeTracker.Clear();
        var savedPost = await _db.Posts.FirstOrDefaultAsync(p => p.WorkspaceId == WorkspaceId);
        Assert.NotNull(savedPost);
        Assert.Equal(igRow.Id, savedPost!.TargetInstagramAccountId);
    }

    // ─── 4. Workspace isolation ──────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAssets_DoesNotPromoteIg_IntoAnotherWorkspace()
    {
        // Only WorkspaceId has the connection/page; OtherWorkspaceId has nothing.
        SeedConnectedPageWithoutIg(WorkspaceId);

        await MakeOAuthService().RefreshAssetsAsync(WorkspaceId);

        _db.ChangeTracker.Clear();
        // The promoted IG belongs ONLY to WorkspaceId.
        Assert.Equal(1, await _db.ConnectedInstagramAccounts.CountAsync(i => i.WorkspaceId == WorkspaceId));
        Assert.Equal(0, await _db.ConnectedInstagramAccounts.CountAsync(i => i.WorkspaceId == OtherWorkspaceId));

        // And a refresh request for a workspace with no Meta connection is rejected,
        // never reaching into another workspace's assets.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeOAuthService().RefreshAssetsAsync(OtherWorkspaceId));
    }

    // ─── 5. Disconnect lifecycle still clears the auto-promoted IG ───────────────

    [Fact]
    public async Task Disconnect_SoftDisconnectsAutoPromotedIg_AndCancelsItsPosts()
    {
        SeedConnectedPageWithoutIg(WorkspaceId);
        await MakeOAuthService().RefreshAssetsAsync(WorkspaceId);
        _db.ChangeTracker.Clear();

        var ig = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);
        var scheduled = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            Content = "scheduled ig post",
            Platform = Platform.Instagram,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            TargetInstagramAccountId = ig.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ScheduleArn = "local-polling",
        };
        _db.Posts.Add(scheduled);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await MakeOAuthService().DisconnectAsync(WorkspaceId);

        _db.ChangeTracker.Clear();
        var igAfter = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);
        var postAfter = await _db.Posts.FindAsync(scheduled.Id);

        Assert.False(igAfter.IsConnected);
        Assert.NotNull(igAfter.DisconnectedAt);
        Assert.Equal(PostStatus.Canceled, postAfter!.Status);
    }

    // ─── 6. Facebook Page disconnect disables its derived IG and cancels its posts ──

    [Fact]
    public async Task UpdateConnection_DisconnectingParentPage_DisablesDerivedIg_AndCancelsItsPosts()
    {
        // Connected page + its auto-promoted (derived) IG, with a scheduled IG post.
        SeedConnectedPageWithoutIg(WorkspaceId);
        await MakeOAuthService().RefreshAssetsAsync(WorkspaceId);
        _db.ChangeTracker.Clear();

        var ig = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);
        var scheduled = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            Content = "scheduled ig post",
            Platform = Platform.Instagram,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            TargetInstagramAccountId = ig.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ScheduleArn = "local-polling",
        };
        _db.Posts.Add(scheduled);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Disconnect the parent Facebook Page (Assets-page page-disconnect path: it drops the
        // page from the selection). The derived IG must follow — there is no per-IG opt-out,
        // so disabling the page is the only way to disable its Instagram publishing.
        await MakeOAuthService().UpdateConnectionAsync(
            WorkspaceId,
            selectedPageIds: new List<string>(),
            selectedInstagramIds: new List<string>());

        _db.ChangeTracker.Clear();
        var pageAfter = await _db.ConnectedPages.SingleAsync(p => p.WorkspaceId == WorkspaceId);
        var igAfter = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);
        var postAfter = await _db.Posts.FindAsync(scheduled.Id);

        Assert.False(pageAfter.IsConnected);          // page disconnected
        Assert.False(igAfter.IsConnected);            // derived IG disabled with it
        Assert.NotNull(igAfter.DisconnectedAt);
        Assert.Equal(PostStatus.Canceled, postAfter!.Status);   // its future post canceled
    }

    // ─── 7. Independent IG opt-out is impossible — refresh re-promotes ──────────────

    [Fact]
    public async Task UpdateConnection_CannotIndependentlyDisconnectIg_WhileParentPageStaysConnected()
    {
        // Connected page + its auto-promoted IG.
        SeedConnectedPageWithoutIg(WorkspaceId);
        await MakeOAuthService().RefreshAssetsAsync(WorkspaceId);
        _db.ChangeTracker.Clear();

        // Attempt the old "disconnect just this IG" affordance: keep the page selected but
        // drop the IG from the selection. selectedInstagramIds no longer gates promotion, so
        // the IG linked to the still-connected page stays connected — there is no opt-out.
        await MakeOAuthService().UpdateConnectionAsync(
            WorkspaceId,
            selectedPageIds: new List<string> { PageId },
            selectedInstagramIds: new List<string>());

        _db.ChangeTracker.Clear();
        var igAfter = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);
        Assert.True(igAfter.IsConnected);
        Assert.Null(igAfter.DisconnectedAt);

        // And a subsequent refresh keeps it promoted (idempotent, never re-disconnects).
        await MakeOAuthService().RefreshAssetsAsync(WorkspaceId);
        _db.ChangeTracker.Clear();
        var igAfterRefresh = await _db.ConnectedInstagramAccounts.SingleAsync(i => i.WorkspaceId == WorkspaceId);
        Assert.True(igAfterRefresh.IsConnected);
    }

    // ─── routed fake Graph API ───────────────────────────────────────────────────

    /// <summary>
    /// Minimal Meta Graph router covering exactly the endpoints the service touches:
    ///   • /me?fields=id,name           → stable identity
    ///   • /me/accounts                 → one page (Posts Dev Page) WITH a page token
    ///   • /{pageId}?fields=...ig...     → that page's linked IG business account
    ///   • /me/permissions, everything else → empty 200
    /// </summary>
    private sealed class GraphApiFakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            string json;

            if (url.Contains("/me/accounts"))
            {
                json = JsonSerializer.Serialize(new
                {
                    data = new[]
                    {
                        new { id = PageId, name = PageName, category = "Software", access_token = PageToken },
                    },
                });
            }
            else if (url.Contains("/me?") || url.EndsWith("/me"))
            {
                json = JsonSerializer.Serialize(new { id = "meta-user-1", name = "Andrey Katz" });
            }
            else if (url.Contains($"/{PageId}?") && url.Contains("instagram_business_account"))
            {
                json = JsonSerializer.Serialize(new
                {
                    name = PageName,
                    instagram_business_account = new
                    {
                        id = IgBusinessId,
                        username = IgUsername,
                        name = "App Questor",
                        profile_picture_url = "https://example.com/appquestor.jpg",
                    },
                });
            }
            else
            {
                json = "{}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }
}
