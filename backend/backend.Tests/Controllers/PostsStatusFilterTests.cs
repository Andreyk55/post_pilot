using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Tests for the My Posts status filtering on GET /api/posts:
/// - single `status` returns exactly that status
/// - `statusGroup=inProgress` collapses Publishing + Processing + RetryPending
/// - the in-progress group never leaks Scheduled/Published/Failed/Canceled
/// - provider/workspace visibility filtering still applies on top of status filtering
/// </summary>
public class PostsStatusFilterTests : IDisposable
{
    private static readonly Guid TestWorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private readonly AppDbContext _context;
    private readonly PostsController _controller;
    private Guid _connectedPageId;

    public PostsStatusFilterTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);

        var schedulerMock = new Mock<IPostScheduler>();
        var insightsMock = new Mock<IFacebookInsightsService>();
        var workspaceMock = new Mock<ICurrentWorkspaceProvider>();
        workspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspaceId);

        _controller = new PostsController(
            _context,
            schedulerMock.Object,
            insightsMock.Object,
            workspaceMock.Object,
            NullLogger<PostsController>.Instance);
    }

    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Seeds a connected Meta connection + Facebook page so seeded posts pass the
    /// provider-visibility filter in GetPosts. Returns the connected page id.
    /// </summary>
    private async Task<Guid> SeedConnectedPageAsync()
    {
        var connection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            UserId = Guid.NewGuid(),
            Provider = ProviderType.Meta,
            AccessToken = "user-tok",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsConnected = true,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            MetaConnectionId = connection.Id,
            PageId = "fb-page-test",
            Name = "Test Page",
            AccessToken = "tok",
            CreatedAt = DateTime.UtcNow,
            IsConnected = true,
        };
        _context.MetaConnections.Add(connection);
        _context.ConnectedPages.Add(page);
        await _context.SaveChangesAsync();
        return page.Id;
    }

    private Post NewPost(PostStatus status, Guid pageId, Guid? workspaceId = null)
    {
        return new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId ?? TestWorkspaceId,
            Content = $"post-{status}",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = status,
            TargetPageId = pageId,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>Seeds exactly one post of every status, all visible to the workspace.</summary>
    private async Task SeedOneOfEachStatusAsync()
    {
        _connectedPageId = await SeedConnectedPageAsync();

        foreach (var status in Enum.GetValues<PostStatus>())
        {
            _context.Posts.Add(NewPost(status, _connectedPageId));
        }
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetPosts_StatusScheduled_ReturnsOnlyScheduled()
    {
        await SeedOneOfEachStatusAsync();

        var result = await _controller.GetPosts(page: 1, pageSize: 50, status: PostStatus.Scheduled);

        var response = result.Value;
        Assert.NotNull(response);
        Assert.All(response!.Items, p => Assert.Equal(PostStatus.Scheduled, p.Status));
        Assert.Single(response.Items);
    }

    [Fact]
    public async Task GetPosts_StatusPublished_ReturnsOnlyPublished()
    {
        await SeedOneOfEachStatusAsync();

        var result = await _controller.GetPosts(page: 1, pageSize: 50, status: PostStatus.Published);

        var response = result.Value;
        Assert.NotNull(response);
        Assert.All(response!.Items, p => Assert.Equal(PostStatus.Published, p.Status));
        Assert.Single(response.Items);
    }

    [Fact]
    public async Task GetPosts_StatusGroupInProgress_ReturnsPublishingProcessingRetryPending()
    {
        await SeedOneOfEachStatusAsync();

        var result = await _controller.GetPosts(page: 1, pageSize: 50, statusGroup: "inProgress");

        var response = result.Value;
        Assert.NotNull(response);
        var statuses = response!.Items.Select(p => p.Status).OrderBy(s => s).ToList();
        Assert.Equal(
            new[] { PostStatus.Publishing, PostStatus.RetryPending, PostStatus.Processing }.OrderBy(s => s),
            statuses);
    }

    [Fact]
    public async Task GetPosts_StatusGroupInProgress_ExcludesTerminalAndScheduled()
    {
        await SeedOneOfEachStatusAsync();

        var result = await _controller.GetPosts(page: 1, pageSize: 50, statusGroup: "inProgress");

        var statuses = result.Value!.Items.Select(p => p.Status).ToHashSet();
        Assert.DoesNotContain(PostStatus.Scheduled, statuses);
        Assert.DoesNotContain(PostStatus.Published, statuses);
        Assert.DoesNotContain(PostStatus.Failed, statuses);
        Assert.DoesNotContain(PostStatus.Canceled, statuses);
    }

    [Fact]
    public async Task GetPosts_StatusGroupInProgress_IsCaseInsensitive()
    {
        await SeedOneOfEachStatusAsync();

        var result = await _controller.GetPosts(page: 1, pageSize: 50, statusGroup: "INPROGRESS");

        Assert.Equal(3, result.Value!.Items.Count);
    }

    [Fact]
    public async Task GetPosts_UnknownStatusGroup_ReturnsNothing()
    {
        await SeedOneOfEachStatusAsync();

        var result = await _controller.GetPosts(page: 1, pageSize: 50, statusGroup: "bogus");

        Assert.Empty(result.Value!.Items);
    }

    [Fact]
    public async Task GetPosts_StatusGroupTakesPrecedenceOverStatus()
    {
        await SeedOneOfEachStatusAsync();

        // Both provided: statusGroup wins, so a single-status value is ignored.
        var result = await _controller.GetPosts(
            page: 1, pageSize: 50, status: PostStatus.Scheduled, statusGroup: "inProgress");

        var statuses = result.Value!.Items.Select(p => p.Status).OrderBy(s => s).ToList();
        Assert.Equal(
            new[] { PostStatus.Publishing, PostStatus.RetryPending, PostStatus.Processing }.OrderBy(s => s),
            statuses);
    }

    [Fact]
    public async Task GetPosts_StatusGroupInProgress_StillAppliesProviderVisibility()
    {
        // A connected page (visible) and a disconnected page (hidden) in the same workspace.
        var visiblePageId = await SeedConnectedPageAsync();

        var hiddenConnection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            UserId = Guid.NewGuid(),
            Provider = ProviderType.Meta,
            AccessToken = "tok2",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsConnected = false, // disconnected → posts tied to it are hidden
        };
        var hiddenPage = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            MetaConnectionId = hiddenConnection.Id,
            PageId = "fb-page-hidden",
            Name = "Hidden Page",
            AccessToken = "tok2",
            CreatedAt = DateTime.UtcNow,
            IsConnected = false,
        };
        _context.MetaConnections.Add(hiddenConnection);
        _context.ConnectedPages.Add(hiddenPage);
        await _context.SaveChangesAsync();

        // One Publishing post on each page.
        _context.Posts.Add(NewPost(PostStatus.Publishing, visiblePageId));
        _context.Posts.Add(NewPost(PostStatus.Publishing, hiddenPage.Id));
        await _context.SaveChangesAsync();

        var result = await _controller.GetPosts(page: 1, pageSize: 50, statusGroup: "inProgress");

        // Only the post tied to the connected page is returned.
        var post = Assert.Single(result.Value!.Items);
        Assert.Equal(visiblePageId, post.TargetPageId);
    }
}
