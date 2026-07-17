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
/// API-level enforcement of the Story no-text rule (PostContentRules): Facebook and Instagram
/// Stories have no text/caption field in the UI, so a direct API request that smuggles content
/// (including whitespace-only) into a Story must be rejected — on create (which is also the
/// scheduling and publish-now entry point) AND on update of an existing Story. Feed text
/// behavior must remain unchanged.
/// </summary>
public class PostStoryContentValidationTests : IDisposable
{
    private static readonly Guid TestWorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000ab");

    private readonly AppDbContext _context;
    private readonly PostsController _controller;

    public PostStoryContentValidationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new AppDbContext(options);

        var workspaceMock = new Mock<ICurrentWorkspaceProvider>();
        workspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspaceId);

        var schedulerMock = new Mock<IPostScheduler>();
        schedulerMock.Setup(x => x.ScheduleAsync(It.IsAny<Post>()))
            .ReturnsAsync(new ScheduleResult(true, "test-arn", null));
        schedulerMock.Setup(x => x.RescheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduleResult(true, "test-arn-resched", null));

        _controller = new PostsController(
            _context,
            schedulerMock.Object,
            new Mock<IFacebookInsightsService>().Object,
            workspaceMock.Object,
            new PassThroughMediaGate(),
            NullLogger<PostsController>.Instance);
    }

    public void Dispose() => _context.Dispose();

    // ── Seed helpers ─────────────────────────────────────────────────────────────

    private async Task<ConnectedPage> SeedFacebookPage()
    {
        var metaConnection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            UserId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            AccessToken = "test-token",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            MetaConnectionId = metaConnection.Id,
            PageId = $"fb-page-{Guid.NewGuid():N}",
            Name = "Test FB Page",
            AccessToken = "page-token",
            CreatedAt = DateTime.UtcNow,
        };
        _context.MetaConnections.Add(metaConnection);
        _context.ConnectedPages.Add(page);
        await _context.SaveChangesAsync();
        return page;
    }

    private async Task<ConnectedInstagramAccount> SeedInstagramAccount()
    {
        var metaConnection = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            UserId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            AccessToken = "test-token",
            TokenExpiresAt = DateTime.UtcNow.AddDays(60),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var igAccount = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            MetaConnectionId = metaConnection.Id,
            IgBusinessId = "ig-123",
            Username = "testuser",
            Name = "Test IG",
            PageId = "123456",
            PageName = "Test Page",
            CreatedAt = DateTime.UtcNow,
        };
        _context.MetaConnections.Add(metaConnection);
        _context.ConnectedInstagramAccounts.Add(igAccount);
        await _context.SaveChangesAsync();
        return igAccount;
    }

    private async Task<Media> SeedUploadedMedia(MediaType mediaType = MediaType.Image)
    {
        var (fileName, contentType) = mediaType switch
        {
            MediaType.Video => ("video.mp4", "video/mp4"),
            _ => ("image.jpg", "image/jpeg"),
        };
        var media = new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            StorageProvider = "local-disk",
            StorageKey = $"users/test/workspaces/{TestWorkspaceId:D}/providers/meta-facebook/media/{Guid.NewGuid():D}/{fileName}",
            ContentType = contentType,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        };
        _context.Media.Add(media);
        await _context.SaveChangesAsync();
        return media;
    }

    private async Task<Post> SeedScheduledStory(Platform platform, Guid? targetPageId, Guid? targetIgAccountId)
    {
        var media = await SeedUploadedMedia();
        var post = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = string.Empty,
            MediaUrl = media.StorageKey,
            MediaType = MediaType.Image,
            PostType = PostType.Story,
            Platform = platform,
            TargetPageId = targetPageId,
            TargetInstagramAccountId = targetIgAccountId,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();
        return post;
    }

    private static void AssertStoryTextRejected(ActionResult<PostDto> result, string expectedMessage)
    {
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Equal(expectedMessage, Assert.Single(problemDetails.Errors["content"]));
    }

    // ── Facebook Story: create (also the scheduling + publish-now entry point) ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreateFacebookStory_NullOrEmptyContent_Succeeds(string? content)
    {
        var page = await SeedFacebookPage();
        var media = await SeedUploadedMedia();

        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            PostType: PostType.Story,
            TargetPageId: page.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(created.Value);
        Assert.Equal(string.Empty, post.Content); // entity convention: stored as string.Empty
        Assert.Equal(PostType.Story, post.PostType);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("   \t\n  ")]
    public async Task CreateFacebookStory_NonEmptyContent_IsRejected_NotSilentlyDropped(string content)
    {
        var page = await SeedFacebookPage();
        var media = await SeedUploadedMedia();

        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            PostType: PostType.Story,
            TargetPageId: page.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        AssertStoryTextRejected(result, "Facebook Story posts do not support post text.");
        Assert.Empty(await _context.Posts.ToListAsync()); // nothing persisted
    }

    // ── Instagram Story: create ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreateInstagramStory_NullOrEmptyContent_Succeeds(string? content)
    {
        var igAccount = await SeedInstagramAccount();
        var media = await SeedUploadedMedia();

        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            PostType: PostType.Story,
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(created.Value);
        Assert.Equal(string.Empty, post.Content);
        Assert.Equal(PostType.Story, post.PostType);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("   \t\n  ")]
    public async Task CreateInstagramStory_NonEmptyContent_IsRejected_NotSilentlyDropped(string content)
    {
        var igAccount = await SeedInstagramAccount();
        var media = await SeedUploadedMedia();

        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            PostType: PostType.Story,
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        AssertStoryTextRejected(result, "Instagram Story posts do not support captions.");
        Assert.Empty(await _context.Posts.ToListAsync());
    }

    // ── Update: cannot ADD text to an existing Story (placement is immutable) ───

    [Theory]
    [InlineData("hello")]
    [InlineData(" ")]
    [InlineData("\n")]
    public async Task UpdateFacebookStory_AddingText_IsRejected_AndRowUnchanged(string content)
    {
        var page = await SeedFacebookPage();
        var story = await SeedScheduledStory(Platform.Facebook, page.Id, null);
        var media = await SeedUploadedMedia();

        var request = new UpdatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Facebook,
            ScheduledAt: story.ScheduledAt,
            TargetPageId: page.Id,
            MediaId: media.Id);

        var result = await _controller.UpdatePost(story.Id, request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Equal("Facebook Story posts do not support post text.", Assert.Single(problemDetails.Errors["content"]));

        var stored = await _context.Posts.AsNoTracking().SingleAsync(p => p.Id == story.Id);
        Assert.Equal(string.Empty, stored.Content);
        Assert.Equal(story.MediaUrl, stored.MediaUrl);
    }

    [Theory]
    [InlineData("a caption")]
    [InlineData("\t")]
    public async Task UpdateInstagramStory_AddingText_IsRejected(string content)
    {
        var igAccount = await SeedInstagramAccount();
        var story = await SeedScheduledStory(Platform.Instagram, null, igAccount.Id);
        var media = await SeedUploadedMedia();

        var request = new UpdatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: story.ScheduledAt,
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id);

        var result = await _controller.UpdatePost(story.Id, request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Equal("Instagram Story posts do not support captions.", Assert.Single(problemDetails.Errors["content"]));

        var stored = await _context.Posts.AsNoTracking().SingleAsync(p => p.Id == story.Id);
        Assert.Equal(string.Empty, stored.Content);
    }

    [Fact]
    public async Task UpdateFacebookStory_EmptyContent_Succeeds()
    {
        var page = await SeedFacebookPage();
        var story = await SeedScheduledStory(Platform.Facebook, page.Id, null);
        var media = await SeedUploadedMedia();

        var request = new UpdatePostRequest(
            Content: string.Empty,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Facebook,
            ScheduledAt: story.ScheduledAt.AddHours(1),
            TargetPageId: page.Id,
            MediaId: media.Id);

        var result = await _controller.UpdatePost(story.Id, request);

        Assert.IsType<NoContentResult>(result);
        var stored = await _context.Posts.AsNoTracking().SingleAsync(p => p.Id == story.Id);
        Assert.Equal(string.Empty, stored.Content);
    }

    // ── Placement isolation: Feed text behavior is unchanged ────────────────────

    [Fact]
    public async Task CreateFacebookFeed_OrdinaryText_StillAccepted()
    {
        var page = await SeedFacebookPage();

        var request = new CreatePostRequest(
            Content: "hello feed",
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: page.Id);

        var result = await _controller.CreatePost(request);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(created.Value);
        Assert.Equal("hello feed", post.Content);
        Assert.Equal(PostType.Feed, post.PostType);
    }

    [Fact]
    public async Task CreateFacebookFeed_WhitespaceOnlyText_StillAccepted()
    {
        // The whitespace rule is Story-only; Feed content is stored verbatim as before.
        var page = await SeedFacebookPage();
        var media = await SeedUploadedMedia();

        var request = new CreatePostRequest(
            Content: " \n ",
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: page.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(created.Value);
        Assert.Equal(" \n ", post.Content);
    }

    [Fact]
    public async Task CreateFacebookFeed_TextOverFiveThousand_StillRejectedByLengthRule()
    {
        var page = await SeedFacebookPage();

        var request = new CreatePostRequest(
            Content: new string('x', 5001),
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: page.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Facebook", problemDetails.Errors["content"][0]);
        Assert.Contains("Max 5000 characters", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreateInstagramFeed_CaptionOverTwentyTwoHundred_StillRejectedByLengthRule()
    {
        var igAccount = await SeedInstagramAccount();
        var media = await SeedUploadedMedia();

        var request = new CreatePostRequest(
            Content: new string('x', 2201),
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Instagram", problemDetails.Errors["content"][0]);
        Assert.Contains("Max 2200 characters", problemDetails.Errors["content"][0]);
    }

    // ── Story media requirement is unchanged ────────────────────────────────────

    [Fact]
    public async Task CreateFacebookStory_WithoutMedia_StillRejectedByMediaRule()
    {
        var page = await SeedFacebookPage();

        var request = new CreatePostRequest(
            Content: null,
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            PostType: PostType.Story,
            TargetPageId: page.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("Stories require exactly one media item", problem.Detail);
    }
}
