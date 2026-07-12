using System.Net;
using System.Text.Json;
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
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Regression tests for the bug where posts targeting a per-asset-disconnected Facebook
/// Page (page deselected on the Assets page while the Meta identity stayed connected)
/// kept showing up in My Posts and the Schedule Posts list.
///
/// GET /api/posts serves BOTH surfaces (My Posts uses status/statusGroup filters, the
/// Schedule Posts right-side list calls it unfiltered), so these tests assert against
/// PostsController.GetPosts directly:
///   - disconnecting one of two pages hides that page's posts from listings
///   - the still-connected page's posts remain visible
///   - active posts on the removed page are auto-canceled; published rows stay in the DB
///   - re-selecting the page resurfaces its historical posts
/// </summary>
public class AssetDisconnectVisibilityTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private const string PageAId = "fb-page-a";
    private const string PageAName = "Posts Dev Page";
    private const string PageBId = "fb-page-b";
    private const string PageBName = "Second Page";

    private readonly AppDbContext _db;
    private readonly Mock<IPostScheduler> _schedulerMock;

    public AssetDisconnectVisibilityTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _schedulerMock = new Mock<IPostScheduler>();
        _schedulerMock
            .Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _db.Dispose();

    // ─── wiring ─────────────────────────────────────────────────────────────────

    private MetaOAuthService MakeOAuthService()
    {
        var httpClient = new HttpClient(new TwoPageGraphApiFakeHandler());
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

    /// <summary>One connected Meta connection with TWO connected pages in the workspace.</summary>
    private MetaConnection SeedTwoConnectedPages(out ConnectedPage pageA, out ConnectedPage pageB)
    {
        var connection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
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
        pageA = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            MetaConnectionId = connection.Id,
            PageId = PageAId,
            Name = PageAName,
            AccessToken = "page-a-token",
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
            Status = ConnectionStatus.Active,
        };
        pageB = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            MetaConnectionId = connection.Id,
            PageId = PageBId,
            Name = PageBName,
            AccessToken = "page-b-token",
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
            Status = ConnectionStatus.Active,
        };
        connection.Pages.Add(pageA);
        connection.Pages.Add(pageB);
        _db.MetaConnections.Add(connection);
        _db.SaveChanges();
        return connection;
    }

    private Post AddPost(PostStatus status, Guid targetPageId)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            Content = $"post-{Guid.NewGuid()}",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = status,
            TargetPageId = targetPageId,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ScheduleArn = "local-polling",
        };
        _db.Posts.Add(post);
        _db.SaveChanges();
        return post;
    }

    // ─── full flow: disconnect one of two pages via the Assets-page path ─────────

    [Fact]
    public async Task DisconnectOnePage_HidesItsPostsFromListings_KeepsOtherPageVisible()
    {
        SeedTwoConnectedPages(out var pageA, out var pageB);
        var publishedA = AddPost(PostStatus.Published, pageA.Id);
        var scheduledA = AddPost(PostStatus.Scheduled, pageA.Id);
        var publishedB = AddPost(PostStatus.Published, pageB.Id);
        var scheduledB = AddPost(PostStatus.Scheduled, pageB.Id);

        // Deselect page A on the Assets page: only page B stays selected.
        await MakeOAuthService().UpdateConnectionAsync(
            WorkspaceId,
            selectedPageIds: new List<string> { PageBId },
            selectedInstagramIds: new List<string>());

        _db.ChangeTracker.Clear();

        // Unfiltered listing (Schedule Posts right-side list).
        var all = (await MakePostsController().GetPosts(page: 1, pageSize: 50)).Value!;
        Assert.All(all.Items, p => Assert.Equal(pageB.Id, p.TargetPageId));
        Assert.DoesNotContain(all.Items, p => p.Id == publishedA.Id);
        Assert.DoesNotContain(all.Items, p => p.Id == scheduledA.Id);
        Assert.Contains(all.Items, p => p.Id == publishedB.Id);
        Assert.Contains(all.Items, p => p.Id == scheduledB.Id);

        // Status-filtered listing (My Posts tabs).
        var published = (await MakePostsController().GetPosts(page: 1, pageSize: 50, status: PostStatus.Published)).Value!;
        var publishedItem = Assert.Single(published.Items);
        Assert.Equal(publishedB.Id, publishedItem.Id);
    }

    [Fact]
    public async Task DisconnectOnePage_CancelsItsActivePosts_KeepsPublishedRowInDb()
    {
        SeedTwoConnectedPages(out var pageA, out _);
        var publishedA = AddPost(PostStatus.Published, pageA.Id);
        var scheduledA = AddPost(PostStatus.Scheduled, pageA.Id);

        await MakeOAuthService().UpdateConnectionAsync(
            WorkspaceId,
            selectedPageIds: new List<string> { PageBId },
            selectedInstagramIds: new List<string>());

        _db.ChangeTracker.Clear();

        // Scheduled post on the removed page is auto-canceled; the published one is untouched.
        var scheduledAfter = await _db.Posts.FindAsync(scheduledA.Id);
        Assert.Equal(PostStatus.Canceled, scheduledAfter!.Status);
        var publishedAfter = await _db.Posts.FindAsync(publishedA.Id);
        Assert.Equal(PostStatus.Published, publishedAfter!.Status);

        // The page row and the hidden posts survive in the DB (hidden ≠ deleted).
        var pageAfter = await _db.ConnectedPages.FindAsync(pageA.Id);
        Assert.False(pageAfter!.IsConnected);
        Assert.NotNull(pageAfter.DisconnectedAt);
    }

    [Fact]
    public async Task ReconnectPage_ResurfacesItsHistoricalPosts()
    {
        SeedTwoConnectedPages(out var pageA, out var pageB);
        var publishedA = AddPost(PostStatus.Published, pageA.Id);

        var service = MakeOAuthService();
        await service.UpdateConnectionAsync(
            WorkspaceId,
            selectedPageIds: new List<string> { PageBId },
            selectedInstagramIds: new List<string>());

        _db.ChangeTracker.Clear();
        var whileDisconnected = (await MakePostsController().GetPosts(page: 1, pageSize: 50)).Value!;
        Assert.DoesNotContain(whileDisconnected.Items, p => p.Id == publishedA.Id);

        // Re-select page A: the SAME asset row flips back to connected.
        await MakeOAuthService().UpdateConnectionAsync(
            WorkspaceId,
            selectedPageIds: new List<string> { PageAId, PageBId },
            selectedInstagramIds: new List<string>());

        _db.ChangeTracker.Clear();
        var pageAfter = await _db.ConnectedPages.FindAsync(pageA.Id);
        Assert.True(pageAfter!.IsConnected);

        var afterReconnect = (await MakePostsController().GetPosts(page: 1, pageSize: 50)).Value!;
        Assert.Contains(afterReconnect.Items, p => p.Id == publishedA.Id);
    }

    // ─── direct regression: the exact broken DB shape, no OAuth flow ─────────────

    [Fact]
    public async Task GetPosts_HidesPostsOnDisconnectedPage_EvenWhenParentConnectionIsStillActive()
    {
        // The bug: the visibility filter only checked MetaConnection.IsConnected, so a
        // page-level disconnect (parent connection still active) leaked the page's posts.
        // DisconnectedAt AFTER the connection's ConnectedAt marks a per-asset deselect
        // (as opposed to a provider-level disconnect stamp, which a later same-account
        // reconnect would supersede by re-stamping ConnectedAt).
        SeedTwoConnectedPages(out var pageA, out var pageB);
        pageA.IsConnected = false;
        pageA.DisconnectedAt = DateTime.UtcNow.AddMinutes(1);
        _db.SaveChanges();

        var hidden = AddPost(PostStatus.Published, pageA.Id);
        var visible = AddPost(PostStatus.Published, pageB.Id);

        var result = (await MakePostsController().GetPosts(page: 1, pageSize: 50)).Value!;

        var item = Assert.Single(result.Items);
        Assert.Equal(visible.Id, item.Id);
        Assert.Equal(1, result.TotalCount);

        // Hidden, not deleted.
        Assert.NotNull(await _db.Posts.FindAsync(hidden.Id));
    }

    [Fact]
    public async Task GetPosts_HidesPostsOnDisconnectedInstagramAccount_EvenWhenParentConnectionIsStillActive()
    {
        var connection = SeedTwoConnectedPages(out var pageA, out _);
        var ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            MetaConnectionId = connection.Id,
            IgBusinessId = "ig-biz-1",
            Username = "testuser",
            PageId = PageAId,
            PageName = PageAName,
            CreatedAt = DateTime.UtcNow,
            IsConnected = false,
            DisconnectedAt = DateTime.UtcNow.AddMinutes(1), // deselected AFTER the connection session started
        };
        _db.ConnectedInstagramAccounts.Add(ig);
        _db.SaveChanges();

        var igPost = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            Content = "ig post",
            Platform = Platform.Instagram,
            PostType = PostType.Feed,
            Status = PostStatus.Published,
            TargetInstagramAccountId = ig.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(igPost);
        var visible = AddPost(PostStatus.Published, pageA.Id);

        var result = (await MakePostsController().GetPosts(page: 1, pageSize: 50)).Value!;

        var item = Assert.Single(result.Items);
        Assert.Equal(visible.Id, item.Id);
    }

    // ─── routed fake Graph API: /me/accounts serves BOTH pages ───────────────────

    private sealed class TwoPageGraphApiFakeHandler : HttpMessageHandler
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
                        new { id = PageAId, name = PageAName, category = "Software", access_token = "page-a-token" },
                        new { id = PageBId, name = PageBName, category = "Software", access_token = "page-b-token" },
                    },
                });
            }
            else if (url.Contains("/me?") || url.EndsWith("/me"))
            {
                json = JsonSerializer.Serialize(new { id = "meta-user-1", name = "Andrey Katz" });
            }
            else
            {
                // Page IG-discovery lookups etc.: no linked IG accounts.
                json = "{}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }
}
