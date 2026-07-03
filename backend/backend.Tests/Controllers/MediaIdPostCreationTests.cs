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
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Pins the media-privacy redesign's post create/update contract: the frontend submits
/// <c>MediaId</c> (never a raw StorageKey), and the server resolves it to the internal
/// StorageKey scoped to the CURRENT workspace before anything else runs. Uses
/// <see cref="PassThroughMediaGate"/> so these tests isolate the MediaId-resolution step
/// (which runs before the media validation gate) from media-content validation rules,
/// which are covered elsewhere (PostCreateMediaGateTests / PostUpdateMediaGateTests).
/// </summary>
public class MediaIdPostCreationTests : IDisposable
{
    private static readonly Guid WorkspaceId = Guid.NewGuid();
    private static readonly Guid ForeignWorkspaceId = Guid.NewGuid();

    private readonly AppDbContext _db;
    private readonly PostsController _controller;

    public MediaIdPostCreationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        var scheduler = new Mock<IPostScheduler>();
        scheduler.Setup(s => s.ScheduleAsync(It.IsAny<Post>())).ReturnsAsync(new ScheduleResult(true, "arn", null));
        scheduler.Setup(s => s.RescheduleAsync(It.IsAny<Post>())).ReturnsAsync(new ScheduleResult(true, "arn", null));

        var workspace = new Mock<ICurrentWorkspaceProvider>();
        workspace.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceId);

        _controller = new PostsController(
            _db,
            scheduler.Object,
            Mock.Of<IFacebookInsightsService>(),
            workspace.Object,
            new PassThroughMediaGate(),
            NullLogger<PostsController>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private Guid SeedFacebookPage(Guid workspaceId)
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Provider = ProviderType.Meta, IsConnected = true };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, MetaConnectionId = conn.Id,
            PageId = "FBPAGE", Name = "FB", AccessToken = "TOKEN", IsConnected = true,
        };
        _db.Add(conn); _db.Add(page); _db.SaveChanges();
        return page.Id;
    }

    private Media SeedMedia(Guid workspaceId, string storageKey, string contentType = "image/jpeg")
    {
        var media = new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StorageProvider = "local-disk",
            StorageKey = storageKey,
            ContentType = contentType,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        };
        _db.Media.Add(media);
        _db.SaveChanges();
        return media;
    }

    // ── Single-media CreatePost ─────────────────────────────────────────────────

    [Fact]
    public async Task CreatePost_with_own_workspace_MediaId_succeeds_and_resolves_StorageKey()
    {
        var pageId = SeedFacebookPage(WorkspaceId);
        var media = SeedMedia(WorkspaceId, "users/u/workspaces/w/providers/meta-facebook/media/m/photo.jpg");

        var request = new CreatePostRequest(
            Content: "hello", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<PostDto>(created.Value);

        // The resolved StorageKey never leaves the backend — the response only carries a
        // mediaId-based preview URL.
        Assert.DoesNotContain(media.StorageKey, dto.MediaUrl ?? string.Empty);
        Assert.DoesNotContain("/api/media/files/", dto.MediaUrl ?? string.Empty);

        var savedPost = await _db.Posts.SingleAsync();
        Assert.Equal(media.StorageKey, savedPost.MediaUrl);
    }

    [Fact]
    public async Task CreatePost_with_unknown_MediaId_returns_404_MediaNotFound()
    {
        var pageId = SeedFacebookPage(WorkspaceId);

        var request = new CreatePostRequest(
            Content: "hello", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId,
            MediaId: Guid.NewGuid());

        var result = await _controller.CreatePost(request, CancellationToken.None);

        var problemResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problemResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(problemResult.Value);
        Assert.Equal("MEDIA_NOT_FOUND", problem.Extensions["code"]);
        Assert.Empty(_db.Posts); // nothing persisted
    }

    [Fact]
    public async Task CreatePost_with_foreign_workspace_MediaId_returns_404_not_403()
    {
        var pageId = SeedFacebookPage(WorkspaceId);
        var foreignMedia = SeedMedia(ForeignWorkspaceId, "media/foreign.jpg");

        var request = new CreatePostRequest(
            Content: "hello", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId,
            MediaId: foreignMedia.Id);

        var result = await _controller.CreatePost(request, CancellationToken.None);

        var problemResult = Assert.IsType<ObjectResult>(result.Result);
        // Same 404 as "unknown" — never a 403 that would confirm the id exists elsewhere.
        Assert.Equal(StatusCodes.Status404NotFound, problemResult.StatusCode);
        Assert.Empty(_db.Posts);
    }

    // ── Carousel CreatePost (MediaItems[].MediaId) ──────────────────────────────

    [Fact]
    public async Task CreatePost_carousel_with_own_workspace_MediaIds_succeeds()
    {
        var pageId = SeedFacebookPage(WorkspaceId);
        var media1 = SeedMedia(WorkspaceId, "media/carousel-1.jpg");
        var media2 = SeedMedia(WorkspaceId, "media/carousel-2.jpg");

        var request = new CreatePostRequest(
            Content: "carousel", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId,
            MediaItems: new List<CreatePostMediaItem>
            {
                new(MediaUrl: null, MediaType: MediaType.Image, Order: 0, MediaId: media1.Id),
                new(MediaUrl: null, MediaType: MediaType.Image, Order: 1, MediaId: media2.Id),
            });

        var result = await _controller.CreatePost(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<PostDto>(created.Value);
        Assert.Equal(2, dto.MediaItems!.Count);
        Assert.All(dto.MediaItems!, item => Assert.DoesNotContain("/api/media/files/", item.MediaUrl ?? string.Empty));

        var savedPost = await _db.Posts.Include(p => p.MediaItems).SingleAsync();
        Assert.Equal(media1.StorageKey, savedPost.MediaItems.Single(m => m.Order == 0).MediaUrl);
        Assert.Equal(media2.StorageKey, savedPost.MediaItems.Single(m => m.Order == 1).MediaUrl);
    }

    [Fact]
    public async Task CreatePost_carousel_with_one_unknown_MediaId_rejects_whole_request()
    {
        var pageId = SeedFacebookPage(WorkspaceId);
        var media1 = SeedMedia(WorkspaceId, "media/carousel-ok.jpg");

        var request = new CreatePostRequest(
            Content: "carousel", MediaUrl: null, MediaType: null, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId,
            MediaItems: new List<CreatePostMediaItem>
            {
                new(MediaUrl: null, MediaType: MediaType.Image, Order: 0, MediaId: media1.Id),
                new(MediaUrl: null, MediaType: MediaType.Image, Order: 1, MediaId: Guid.NewGuid()),
            });

        var result = await _controller.CreatePost(request, CancellationToken.None);

        var problemResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status404NotFound, problemResult.StatusCode);
        Assert.Empty(_db.Posts);
    }

    // ── UpdatePost ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePost_with_own_workspace_MediaId_resolves_StorageKey()
    {
        var pageId = SeedFacebookPage(WorkspaceId);
        var originalMedia = SeedMedia(WorkspaceId, "media/original.jpg");
        var newMedia = SeedMedia(WorkspaceId, "media/replacement.jpg");

        var post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, Content = "old", MediaUrl = originalMedia.StorageKey,
            MediaType = MediaType.Image, Platform = Platform.Facebook, PostType = PostType.Feed,
            TargetPageId = pageId, Status = PostStatus.Scheduled, ScheduledAt = DateTime.UtcNow.AddHours(2),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post);
        await _db.SaveChangesAsync();

        var request = new UpdatePostRequest(
            Content: "updated", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: post.ScheduledAt, TargetPageId: pageId, MediaId: newMedia.Id);

        var result = await _controller.UpdatePost(post.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var updated = await _db.Posts.SingleAsync(p => p.Id == post.Id);
        Assert.Equal(newMedia.StorageKey, updated.MediaUrl);
    }

    [Fact]
    public async Task UpdatePost_with_foreign_workspace_MediaId_returns_404_and_does_not_mutate()
    {
        var pageId = SeedFacebookPage(WorkspaceId);
        var originalMedia = SeedMedia(WorkspaceId, "media/original2.jpg");
        var foreignMedia = SeedMedia(ForeignWorkspaceId, "media/foreign2.jpg");

        var post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, Content = "old", MediaUrl = originalMedia.StorageKey,
            MediaType = MediaType.Image, Platform = Platform.Facebook, PostType = PostType.Feed,
            TargetPageId = pageId, Status = PostStatus.Scheduled, ScheduledAt = DateTime.UtcNow.AddHours(2),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post);
        await _db.SaveChangesAsync();

        var request = new UpdatePostRequest(
            Content: "updated", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: post.ScheduledAt, TargetPageId: pageId, MediaId: foreignMedia.Id);

        var result = await _controller.UpdatePost(post.Id, request, CancellationToken.None);

        var problemResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problemResult.StatusCode);

        var unchanged = await _db.Posts.SingleAsync(p => p.Id == post.Id);
        Assert.Equal(originalMedia.StorageKey, unchanged.MediaUrl); // untouched
    }
}
