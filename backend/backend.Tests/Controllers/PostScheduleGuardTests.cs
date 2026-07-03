using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// M6: server-side schedule validation (past / far-future) and the per-workspace active
/// scheduled-post cap, enforced on create and update.
/// </summary>
public class PostScheduleGuardTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private readonly AppDbContext _db;
    private readonly Mock<IPostScheduler> _scheduler = new();

    public PostScheduleGuardTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        _scheduler.Setup(s => s.ScheduleAsync(It.IsAny<Post>()))
            .ReturnsAsync(new ScheduleResult(true, "arn", null));
        _scheduler.Setup(s => s.RescheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduleResult(true, "arn", null));
    }

    public void Dispose() => _db.Dispose();

    private PostsController NewController(SchedulingOptions options)
    {
        var workspace = new Mock<ICurrentWorkspaceProvider>();
        workspace.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ws);

        var controller = new PostsController(
            _db, _scheduler.Object, Mock.Of<IFacebookInsightsService>(),
            workspace.Object, new PassThroughMediaGate(), NullLogger<PostsController>.Instance, options);

        // HttpContext.RequestAborted is read by the cap check.
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static string? CodeOf(ObjectResult result)
        => (result.Value as ProblemDetails)?.Extensions.TryGetValue("code", out var c) == true ? c as string : null;

    /// <summary>Seeds N active (Scheduled) posts so the workspace sits at a known active count.</summary>
    private void SeedActivePosts(int count, PostStatus status = PostStatus.Scheduled)
    {
        for (var i = 0; i < count; i++)
        {
            _db.Posts.Add(new Post
            {
                Id = Guid.NewGuid(), WorkspaceId = Ws, Content = $"p{i}",
                MediaType = MediaType.None, PostType = PostType.Feed, Platform = Platform.Facebook,
                ScheduledAt = DateTime.UtcNow.AddHours(1), Status = status,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        _db.SaveChanges();
    }

    private Guid SeedConnectedFbPage()
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta, IsConnected = true };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, MetaConnectionId = conn.Id,
            PageId = "FBPAGE", Name = "FB", AccessToken = "TOKEN", IsConnected = true,
        };
        _db.Add(conn); _db.Add(page); _db.SaveChanges();
        return page.Id;
    }

    private Post SeedScheduledPost(Guid? targetPageId = null)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, Content = "orig",
            MediaType = MediaType.None, PostType = PostType.Feed, Platform = Platform.Facebook,
            ScheduledAt = DateTime.UtcNow.AddHours(3), TargetPageId = targetPageId,
            Status = PostStatus.Scheduled, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post);
        _db.SaveChanges();
        return post;
    }

    // ── Create: timing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePost_PastScheduledAt_IsRejected()
    {
        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddMinutes(-10), PostType: PostType.Feed);

        var result = await NewController(new SchedulingOptions()).CreatePost(req);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        Assert.Equal(SchedulingCodes.ScheduledAtInPast, CodeOf(obj));
        Assert.Empty(await _db.Posts.ToListAsync());
    }

    [Fact]
    public async Task CreatePost_WithinGrace_IsAccepted()
    {
        var pageId = SeedConnectedFbPage();
        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddMinutes(-1), PostType: PostType.Feed, TargetPageId: pageId);

        var result = await NewController(new SchedulingOptions { PastGraceMinutes = 2 }).CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_TooFarFuture_IsRejected()
    {
        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddDays(400), PostType: PostType.Feed);

        var result = await NewController(new SchedulingOptions { MaxFutureDays = 365 }).CreatePost(req);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        Assert.Equal(SchedulingCodes.ScheduledAtTooFar, CodeOf(obj));
    }

    // ── Create: cap ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePost_AtCap_IsRejected()
    {
        SeedActivePosts(2);
        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed);

        var result = await NewController(new SchedulingOptions { MaxActiveScheduledPostsPerWorkspace = 2 }).CreatePost(req);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, obj.StatusCode);
        Assert.Equal(SchedulingCodes.ScheduledPostLimitReached, CodeOf(obj));
        Assert.Equal(2, await _db.Posts.CountAsync()); // nothing added
    }

    [Fact]
    public async Task CreatePost_BelowCap_Succeeds()
    {
        SeedActivePosts(1);
        var pageId = SeedConnectedFbPage();
        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId);

        var result = await NewController(new SchedulingOptions { MaxActiveScheduledPostsPerWorkspace = 5 }).CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // ── Update: timing ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePost_ToPastScheduledAt_IsRejected()
    {
        var post = SeedScheduledPost();
        var req = new UpdatePostRequest(
            Content: "edit", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddMinutes(-10));

        var result = await NewController(new SchedulingOptions()).UpdatePost(post.Id, req);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        Assert.Equal(SchedulingCodes.ScheduledAtInPast, CodeOf(obj));
    }

    [Fact]
    public async Task UpdatePost_ToTooFarFuture_IsRejected()
    {
        var post = SeedScheduledPost();
        var req = new UpdatePostRequest(
            Content: "edit", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddDays(400));

        var result = await NewController(new SchedulingOptions { MaxFutureDays = 365 }).UpdatePost(post.Id, req);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(SchedulingCodes.ScheduledAtTooFar, CodeOf(obj));
    }

    // ── Update: cap ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePost_OverCap_IsRejected()
    {
        // 2 OTHER active posts + the edited one = workspace above a cap of 2 (excluding self = 2).
        SeedActivePosts(2);
        var post = SeedScheduledPost();
        var req = new UpdatePostRequest(
            Content: "edit", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(2));

        var result = await NewController(new SchedulingOptions { MaxActiveScheduledPostsPerWorkspace = 2 }).UpdatePost(post.Id, req);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, obj.StatusCode);
        Assert.Equal(SchedulingCodes.ScheduledPostLimitReached, CodeOf(obj));
    }

    [Fact]
    public async Task UpdatePost_TerminalPostsDoNotCountTowardCap()
    {
        // Cap 2, but the only ACTIVE post is the one we're editing; terminals must not count.
        var pageId = SeedConnectedFbPage();
        SeedActivePosts(3, PostStatus.Published);
        SeedActivePosts(3, PostStatus.Failed);
        SeedActivePosts(3, PostStatus.Canceled);
        var post = SeedScheduledPost(pageId);

        var req = new UpdatePostRequest(
            Content: "edit", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(4), TargetPageId: pageId);

        var result = await NewController(new SchedulingOptions { MaxActiveScheduledPostsPerWorkspace = 2 }).UpdatePost(post.Id, req);

        Assert.IsType<NoContentResult>(result); // succeeds — terminals didn't block the edit
    }

    // ── ScheduleGuard active-status counting (direct) ────────────────────────────

    [Fact]
    public async Task ActiveCap_counts_only_scheduled_retrypending_processing()
    {
        SeedActivePosts(1, PostStatus.Scheduled);
        SeedActivePosts(1, PostStatus.RetryPending);
        SeedActivePosts(1, PostStatus.Processing);
        SeedActivePosts(5, PostStatus.Published);
        SeedActivePosts(5, PostStatus.Failed);
        SeedActivePosts(5, PostStatus.Canceled);

        var guard = new ScheduleGuard(_db, new SchedulingOptions { MaxActiveScheduledPostsPerWorkspace = 3 });

        // 3 active == cap → rejected.
        var atCap = await guard.ValidateActiveCapAsync(Ws);
        Assert.NotNull(atCap);
        Assert.Equal(SchedulingCodes.ScheduledPostLimitReached, atCap!.Value.Code);

        // Same data under a cap of 4 → allowed (only 3 counted, terminals ignored).
        var underCap = new ScheduleGuard(_db, new SchedulingOptions { MaxActiveScheduledPostsPerWorkspace = 4 });
        Assert.Null(await underCap.ValidateActiveCapAsync(Ws));
    }
}
