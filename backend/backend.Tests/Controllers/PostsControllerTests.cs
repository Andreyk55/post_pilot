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

public class PostsControllerTests : IDisposable
{
    // Fixed test workspace id so seeded entities are visible to the workspace-scoped controller.
    internal static readonly Guid TestWorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private readonly AppDbContext _context;
    private readonly Mock<IPostScheduler> _schedulerMock;
    private readonly Mock<IFacebookInsightsService> _insightsMock;
    private readonly Mock<ICurrentWorkspaceProvider> _workspaceMock;
    private readonly PostsController _controller;

    public PostsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _schedulerMock = new Mock<IPostScheduler>();
        _insightsMock = new Mock<IFacebookInsightsService>();
        _workspaceMock = new Mock<ICurrentWorkspaceProvider>();

        _workspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestWorkspaceId);
        _workspaceMock.Setup(x => x.GetCurrentWorkspaceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentWorkspaceInfo(Guid.NewGuid(), TestWorkspaceId, "Test"));

        _schedulerMock.Setup(x => x.ScheduleAsync(It.IsAny<PostPilot.Api.Entities.Post>()))
            .ReturnsAsync(new ScheduleResult(true, "test-arn", null));
        // Reschedule is called whenever UpdatePost detects ScheduledAt changed.
        // Set a default so non-strict mocks don't return null and NRE inside the controller.
        _schedulerMock.Setup(x => x.RescheduleAsync(It.IsAny<PostPilot.Api.Entities.Post>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduleResult(true, "test-arn-resched", null));

        _controller = new PostsController(
            _context,
            _schedulerMock.Object,
            _insightsMock.Object,
            _workspaceMock.Object,
            new PassThroughMediaGate(),
            NullLogger<PostsController>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    /// <summary>
    /// Helper to create a ConnectedInstagramAccount for tests that need one.
    /// </summary>
    private async Task<ConnectedInstagramAccount> CreateTestInstagramAccount()
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
        _context.MetaConnections.Add(metaConnection);

        var connectedPage = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            MetaConnectionId = metaConnection.Id,
            PageId = "123456",
            Name = "Test Page",
            AccessToken = "page-token",
            CreatedAt = DateTime.UtcNow,
        };
        _context.ConnectedPages.Add(connectedPage);

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
        _context.ConnectedInstagramAccounts.Add(igAccount);
        await _context.SaveChangesAsync();
        return igAccount;
    }

    /// <summary>
    /// Helper to create a connected Facebook ConnectedPage for tests that need
    /// to schedule a Facebook post (production requires TargetPageId).
    /// </summary>
    private async Task<ConnectedPage> CreateTestFacebookPage()
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

    private async Task<Media> CreateUploadedMedia(MediaType mediaType)
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

    #region CreatePost Platform-Specific Validation Tests

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.LinkedIn, 3000)]
    [InlineData(Platform.Twitter, 280)]
    public async Task CreatePost_TextAtExactMaxLength_Succeeds(Platform platform, int maxLength)
    {
        // Facebook now requires a TargetPageId; LinkedIn/Twitter don't.
        Guid? targetPageId = platform == Platform.Facebook
            ? (await CreateTestFacebookPage()).Id
            : null;

        var content = new string('x', maxLength);
        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: null,
            Platform: platform,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: targetPageId);

        var result = await _controller.CreatePost(request);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(createdResult.Value);
        Assert.Equal(content, post.Content);
        Assert.Equal(platform, post.Platform);
    }

    [Fact]
    public async Task CreatePost_Instagram_TextAtExactMaxLength_Succeeds()
    {
        var igAccount = await CreateTestInstagramAccount();
        var media = await CreateUploadedMedia(MediaType.Image);
        var content = new string('x', 2200);
        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(createdResult.Value);
        Assert.Equal(content, post.Content);
        Assert.Equal(Platform.Instagram, post.Platform);
    }

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.LinkedIn, 3000)]
    [InlineData(Platform.Twitter, 280)]
    public async Task CreatePost_TextExceedsMaxLength_ReturnsValidationError(Platform platform, int maxLength)
    {
        var content = new string('x', maxLength + 1);
        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: null,
            Platform: platform,
            ScheduledAt: DateTime.UtcNow.AddHours(1));

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.True(problemDetails.Errors.ContainsKey("content"));
        Assert.Contains($"Text is too long for {platform}", problemDetails.Errors["content"][0]);
        Assert.Contains($"Max {maxLength} characters", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Instagram_TextExceedsMaxLength_ReturnsValidationError()
    {
        var content = new string('x', 2201);
        var igAccount = await CreateTestInstagramAccount();
        var media = await CreateUploadedMedia(MediaType.Image);
        var request = new CreatePostRequest(
            Content: content,
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
        Assert.True(problemDetails.Errors.ContainsKey("content"));
        Assert.Contains("Text is too long for Instagram", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Same3000CharText_AcceptedForFacebook_RejectedForInstagram()
    {
        // Placement isolation: one text, two placements — the limit must come from the
        // post's own platform (Facebook Feed 5000 vs Instagram Feed 2200), not a global cap.
        var content = new string('x', 3000);

        var page = await CreateTestFacebookPage();
        var fbResult = await _controller.CreatePost(new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: page.Id));
        Assert.IsType<CreatedAtActionResult>(fbResult.Result);

        var igAccount = await CreateTestInstagramAccount();
        var igMedia = await CreateUploadedMedia(MediaType.Image);
        var igResult = await _controller.CreatePost(new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: igMedia.Id));

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(igResult.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Instagram", problemDetails.Errors["content"][0]);
        Assert.Contains("Max 2200 characters", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Facebook_ImagePostAtExactMaxLength_Succeeds()
    {
        // The 5000 limit applies identically to media posts — text-only is covered by
        // CreatePost_TextAtExactMaxLength_Succeeds.
        var page = await CreateTestFacebookPage();
        var media = await CreateUploadedMedia(MediaType.Image);
        var content = new string('x', 5000);

        var result = await _controller.CreatePost(new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: page.Id,
            MediaId: media.Id));

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(createdResult.Value);
        Assert.Equal(content, post.Content);
    }

    [Fact]
    public async Task CreatePost_Facebook_CarouselAtExactMaxLength_Succeeds_OneOverIsRejected()
    {
        var page = await CreateTestFacebookPage();

        async Task<ActionResult<PostDto>> CreateCarousel(string content)
        {
            var m1 = await CreateUploadedMedia(MediaType.Image);
            var m2 = await CreateUploadedMedia(MediaType.Image);
            return await _controller.CreatePost(new CreatePostRequest(
                Content: content,
                MediaUrl: null,
                MediaType: null,
                Platform: Platform.Facebook,
                ScheduledAt: DateTime.UtcNow.AddHours(1),
                TargetPageId: page.Id,
                MediaItems: new List<CreatePostMediaItem>
                {
                    new(MediaUrl: null, MediaType: MediaType.Image, Order: 0, MediaId: m1.Id),
                    new(MediaUrl: null, MediaType: MediaType.Image, Order: 1, MediaId: m2.Id),
                }));
        }

        var atLimit = await CreateCarousel(new string('x', 5000));
        Assert.IsType<CreatedAtActionResult>(atLimit.Result);

        var overLimit = await CreateCarousel(new string('x', 5001));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overLimit.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Facebook", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Facebook_VideoPostAtExactMaxLength_Succeeds_OneOverIsRejected()
    {
        var page = await CreateTestFacebookPage();

        async Task<ActionResult<PostDto>> CreateVideoPost(string content)
        {
            var media = await CreateUploadedMedia(MediaType.Video);
            return await _controller.CreatePost(new CreatePostRequest(
                Content: content,
                MediaUrl: null,
                MediaType: MediaType.Video,
                Platform: Platform.Facebook,
                ScheduledAt: DateTime.UtcNow.AddHours(1),
                TargetPageId: page.Id,
                MediaId: media.Id));
        }

        var atLimit = await CreateVideoPost(new string('x', 5000));
        Assert.IsType<CreatedAtActionResult>(atLimit.Result);

        var overLimit = await CreateVideoPost(new string('x', 5001));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overLimit.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Facebook", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Instagram_VideoPostAtExactMaxLength_Succeeds_OneOverIsRejected()
    {
        var igAccount = await CreateTestInstagramAccount();

        async Task<ActionResult<PostDto>> CreateVideoPost(string content)
        {
            var media = await CreateUploadedMedia(MediaType.Video);
            return await _controller.CreatePost(new CreatePostRequest(
                Content: content,
                MediaUrl: null,
                MediaType: MediaType.Video,
                Platform: Platform.Instagram,
                ScheduledAt: DateTime.UtcNow.AddHours(1),
                TargetInstagramAccountId: igAccount.Id,
                MediaId: media.Id));
        }

        var atLimit = await CreateVideoPost(new string('x', 2200));
        Assert.IsType<CreatedAtActionResult>(atLimit.Result);

        var overLimit = await CreateVideoPost(new string('x', 2201));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overLimit.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Instagram", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Instagram_CarouselAtExactMaxLength_Succeeds_OneOverIsRejected()
    {
        var igAccount = await CreateTestInstagramAccount();

        async Task<ActionResult<PostDto>> CreateCarousel(string content)
        {
            var m1 = await CreateUploadedMedia(MediaType.Image);
            var m2 = await CreateUploadedMedia(MediaType.Image);
            return await _controller.CreatePost(new CreatePostRequest(
                Content: content,
                MediaUrl: null,
                MediaType: null,
                Platform: Platform.Instagram,
                ScheduledAt: DateTime.UtcNow.AddHours(1),
                TargetInstagramAccountId: igAccount.Id,
                MediaItems: new List<CreatePostMediaItem>
                {
                    new(MediaUrl: null, MediaType: MediaType.Image, Order: 0, MediaId: m1.Id),
                    new(MediaUrl: null, MediaType: MediaType.Image, Order: 1, MediaId: m2.Id),
                }));
        }

        var atLimit = await CreateCarousel(new string('x', 2200));
        Assert.IsType<CreatedAtActionResult>(atLimit.Result);

        var overLimit = await CreateCarousel(new string('x', 2201));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overLimit.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Instagram", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Instagram_EmptyCaptionWithMedia_Succeeds()
    {
        // The Instagram caption stays optional — the limit must not introduce a requirement.
        var igAccount = await CreateTestInstagramAccount();
        var media = await CreateUploadedMedia(MediaType.Image);

        var result = await _controller.CreatePost(new CreatePostRequest(
            Content: "",
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id));

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_Facebook_EmojiCountsAsUtf16CodeUnits()
    {
        // "😀" = 2 UTF-16 code units in both .NET string.Length and JS .length: 4998 x's +
        // emoji is exactly 5000 (accepted); 4999 x's + emoji is 5001 (rejected).
        var page = await CreateTestFacebookPage();
        const string emoji = "\U0001F600";

        var atLimit = await _controller.CreatePost(new CreatePostRequest(
            Content: new string('x', 4998) + emoji,
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: page.Id));
        Assert.IsType<CreatedAtActionResult>(atLimit.Result);

        var overLimit = await _controller.CreatePost(new CreatePostRequest(
            Content: new string('x', 4999) + emoji,
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: page.Id));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overLimit.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Theory]
    [InlineData(Platform.Facebook)]
    [InlineData(Platform.LinkedIn)]
    [InlineData(Platform.Twitter)]
    public async Task CreatePost_NullContent_Succeeds(Platform platform)
    {
        // Posts can have null content (media-only posts).
        // Facebook now requires a TargetPageId; LinkedIn/Twitter don't.
        Guid? targetPageId = platform == Platform.Facebook
            ? (await CreateTestFacebookPage()).Id
            : null;
        var media = await CreateUploadedMedia(MediaType.Image);

        var request = new CreatePostRequest(
            Content: null!,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: platform,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: targetPageId,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.NotNull(createdResult.Value);
    }

    #endregion

    #region Instagram-Specific Validation Tests

    [Fact]
    public async Task CreatePost_Instagram_RequiresTargetAccount()
    {
        var media = await CreateUploadedMedia(MediaType.Image);
        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(409, objectResult.StatusCode);
    }

    [Fact]
    public async Task CreatePost_Instagram_RequiresImage()
    {
        var igAccount = await CreateTestInstagramAccount();

        // No media at all
        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task CreatePost_Instagram_AcceptsVideo()
    {
        // Production now supports Instagram Reels/video posts (IG video publishing was
        // added after this test originally asserted rejection). Test renamed + flipped
        // to match current behaviour.
        var igAccount = await CreateTestInstagramAccount();
        var media = await CreateUploadedMedia(MediaType.Video);

        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: null,
            MediaType: MediaType.Video,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(createdResult.Value);
        Assert.Equal(MediaType.Video, post.MediaType);
        Assert.Equal(Platform.Instagram, post.Platform);
    }

    [Fact]
    public async Task CreatePost_Instagram_RejectsTextOnly()
    {
        var igAccount = await CreateTestInstagramAccount();

        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: null,
            MediaType: MediaType.None,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task CreatePost_Instagram_WithImage_Succeeds()
    {
        var igAccount = await CreateTestInstagramAccount();
        var media = await CreateUploadedMedia(MediaType.Image);

        var request = new CreatePostRequest(
            Content: "Test caption #hashtag",
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var post = Assert.IsType<PostDto>(createdResult.Value);
        Assert.Equal(Platform.Instagram, post.Platform);
        Assert.Equal(igAccount.Id, post.TargetInstagramAccountId);
        Assert.Equal("@testuser", post.TargetInstagramAccountName);
    }

    [Fact]
    public async Task CreatePost_Instagram_DisconnectedAccount_ReturnsConflict()
    {
        var media = await CreateUploadedMedia(MediaType.Image);
        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: Guid.NewGuid(), // Non-existent account
            MediaId: media.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(409, objectResult.StatusCode);
    }

    #endregion

    #region Publisher Routing Tests

    [Fact]
    public void PostPublisherResolver_ReturnsCorrectPublisher()
    {
        // Verify the resolver pattern works for multiple publishers
        var fbPublisher = new Mock<IPostPublisher>();
        fbPublisher.Setup(p => p.SupportedPlatform).Returns(Platform.Facebook);

        var igPublisher = new Mock<IPostPublisher>();
        igPublisher.Setup(p => p.SupportedPlatform).Returns(Platform.Instagram);

        var resolver = new PostPublisherResolver(new[] { fbPublisher.Object, igPublisher.Object });

        Assert.Same(fbPublisher.Object, resolver.GetPublisher(Platform.Facebook));
        Assert.Same(igPublisher.Object, resolver.GetPublisher(Platform.Instagram));
        Assert.Null(resolver.GetPublisher(Platform.Twitter));
    }

    #endregion

    #region UpdatePost Platform-Specific Validation Tests

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.LinkedIn, 3000)]
    [InlineData(Platform.Twitter, 280)]
    public async Task UpdatePost_TextAtExactMaxLength_Succeeds(Platform platform, int maxLength)
    {
        // Facebook requires a TargetPageId on update too.
        Guid? targetPageId = platform == Platform.Facebook
            ? (await CreateTestFacebookPage()).Id
            : null;

        // Create a post first
        var post = new PostPilot.Api.Entities.Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = "Original content",
            Platform = platform,
            TargetPageId = targetPageId,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var content = new string('x', maxLength);
        var request = new UpdatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: null,
            Platform: platform,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: targetPageId);

        var result = await _controller.UpdatePost(post.Id, request);

        Assert.IsType<NoContentResult>(result);

        var updatedPost = await _context.Posts.FindAsync(post.Id);
        Assert.Equal(content, updatedPost!.Content);
    }

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.LinkedIn, 3000)]
    [InlineData(Platform.Twitter, 280)]
    public async Task UpdatePost_TextExceedsMaxLength_ReturnsValidationError(Platform platform, int maxLength)
    {
        // Create a post first
        var post = new PostPilot.Api.Entities.Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = "Original content",
            Platform = platform,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var content = new string('x', maxLength + 1);
        var request = new UpdatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: null,
            Platform: platform,
            ScheduledAt: DateTime.UtcNow.AddHours(1));

        var result = await _controller.UpdatePost(post.Id, request);

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.True(problemDetails.Errors.ContainsKey("content"));
        Assert.Contains($"Text is too long for {platform}", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task UpdatePost_Instagram_CaptionAtExactMaxLength_Succeeds_OneOverIsRejected()
    {
        var igAccount = await CreateTestInstagramAccount();
        var post = new PostPilot.Api.Entities.Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = "Original caption",
            Platform = Platform.Instagram,
            TargetInstagramAccountId = igAccount.Id,
            MediaType = MediaType.Image,
            MediaUrl = "media/original.jpg",
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        async Task<IActionResult> UpdateWithCaption(string content)
        {
            var media = await CreateUploadedMedia(MediaType.Image);
            return await _controller.UpdatePost(post.Id, new UpdatePostRequest(
                Content: content,
                MediaUrl: null,
                MediaType: MediaType.Image,
                Platform: Platform.Instagram,
                ScheduledAt: DateTime.UtcNow.AddHours(1),
                TargetInstagramAccountId: igAccount.Id,
                MediaId: media.Id));
        }

        Assert.IsType<NoContentResult>(await UpdateWithCaption(new string('x', 2200)));

        var overLimit = await UpdateWithCaption(new string('x', 2201));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overLimit);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("Text is too long for Instagram", problemDetails.Errors["content"][0]);
        Assert.Contains("Max 2200 characters", problemDetails.Errors["content"][0]);

        // The stored caption keeps the last valid value.
        var stored = await _context.Posts.FindAsync(post.Id);
        Assert.Equal(2200, stored!.Content.Length);
    }

    // ── Instagram Feed caption hashtag/@mention caps (create + update flow) ──────

    private static string RepeatToken(string token, int count) =>
        string.Join(" ", System.Linq.Enumerable.Repeat(token, count));

    private async Task<ActionResult<PostDto>> CreateInstagramFeedPost(string content)
    {
        var igAccount = await CreateTestInstagramAccount();
        var media = await CreateUploadedMedia(MediaType.Image);
        return await _controller.CreatePost(new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id));
    }

    [Fact]
    public async Task CreatePost_Instagram_CaptionWith30Hashtags_Succeeds_31IsRejected()
    {
        Assert.IsType<CreatedAtActionResult>((await CreateInstagramFeedPost(RepeatToken("#tag", 30))).Result);

        var overCap = await CreateInstagramFeedPost(RepeatToken("#tag", 31));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overCap.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("at most 30 hashtags", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Instagram_CaptionWith20Mentions_Succeeds_21IsRejected()
    {
        Assert.IsType<CreatedAtActionResult>((await CreateInstagramFeedPost(RepeatToken("@user", 20))).Result);

        var overCap = await CreateInstagramFeedPost(RepeatToken("@user", 21));
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(overCap.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("at most 20 @mentions", problemDetails.Errors["content"][0]);
    }

    [Fact]
    public async Task CreatePost_Instagram_CaptionExceedingBothCaps_ReportsEachError()
    {
        var content = RepeatToken("#tag", 31) + " " + RepeatToken("@user", 21);

        var result = await CreateInstagramFeedPost(content);
        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);

        var contentErrors = problemDetails.Errors["content"];
        Assert.Contains(contentErrors, e => e.Contains("at most 30 hashtags"));
        Assert.Contains(contentErrors, e => e.Contains("at most 20 @mentions"));
    }

    [Fact]
    public async Task UpdatePost_Instagram_CaptionOver30Hashtags_IsRejected_AndStoredCaptionUnchanged()
    {
        var igAccount = await CreateTestInstagramAccount();
        var post = new PostPilot.Api.Entities.Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = "Original caption #one",
            Platform = Platform.Instagram,
            TargetInstagramAccountId = igAccount.Id,
            MediaType = MediaType.Image,
            MediaUrl = "media/original.jpg",
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var media = await CreateUploadedMedia(MediaType.Image);
        var result = await _controller.UpdatePost(post.Id, new UpdatePostRequest(
            Content: RepeatToken("#tag", 31),
            MediaUrl: null,
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id,
            MediaId: media.Id));

        var objectResult = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("at most 30 hashtags", problemDetails.Errors["content"][0]);

        var stored = await _context.Posts.FindAsync(post.Id);
        Assert.Equal("Original caption #one", stored!.Content);
    }

    #endregion

    #region DeletePost Status-Based Rules Tests

    private Post CreateTestPost(PostStatus status)
    {
        return new Post
        {
            Id = Guid.NewGuid(),
            // Must match the workspace the controller resolves through ICurrentWorkspaceProvider.
            // Posts seeded without this are invisible to the workspace-scoped controller
            // and every read/update/delete will return 404.
            WorkspaceId = TestWorkspaceId,
            Content = "Test content",
            Platform = Platform.Facebook,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ScheduleArn = "arn:aws:scheduler:test"
        };
    }

    [Fact]
    public async Task DeletePost_Scheduled_Returns409Conflict()
    {
        // Production policy changed: Scheduled/RetryPending/Processing posts must be
        // canceled via POST /api/posts/{id}/cancel before they can be deleted. The
        // single DELETE call no longer cascades cancel-then-delete.
        var post = CreateTestPost(PostStatus.Scheduled);
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);

        // Post remains untouched.
        var unchanged = await _context.Posts.FindAsync(post.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(PostStatus.Scheduled, unchanged.Status);
    }

    [Fact]
    public async Task DeletePost_RetryPending_Returns409Conflict()
    {
        var post = CreateTestPost(PostStatus.RetryPending);
        post.NextRetryAt = DateTime.UtcNow.AddMinutes(5);
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflict.StatusCode);

        var unchanged = await _context.Posts.FindAsync(post.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(PostStatus.RetryPending, unchanged.Status);
    }

    [Fact]
    public async Task DeletePost_Failed_DeletesRecord()
    {
        var post = CreateTestPost(PostStatus.Failed);
        post.ErrorMessage = "Permanent error";
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        Assert.IsType<NoContentResult>(result);

        var deleted = await _context.Posts.FindAsync(post.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task DeletePost_Publishing_Returns409Conflict()
    {
        var post = CreateTestPost(PostStatus.Publishing);
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        var conflictResult = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflictResult.StatusCode);

        // Post should remain untouched
        var unchanged = await _context.Posts.FindAsync(post.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(PostStatus.Publishing, unchanged.Status);
    }

    [Fact]
    public async Task DeletePost_Published_Returns409Conflict()
    {
        var post = CreateTestPost(PostStatus.Published);
        post.PublishedAt = DateTime.UtcNow;
        post.ExternalPostId = "page_post123";
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        var conflictResult = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(409, conflictResult.StatusCode);

        // Post should remain untouched
        var unchanged = await _context.Posts.FindAsync(post.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(PostStatus.Published, unchanged.Status);
    }

    [Fact]
    public async Task DeletePost_Canceled_HardDeletesRecord()
    {
        // Production policy changed: deleting a Canceled post now hard-deletes the
        // row (was idempotent / kept the row in earlier versions).
        var post = CreateTestPost(PostStatus.Canceled);
        post.CanceledAt = DateTime.UtcNow.AddMinutes(-5);
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        Assert.IsType<NoContentResult>(result);

        var deleted = await _context.Posts.FindAsync(post.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task DeletePost_NotFound_Returns404()
    {
        var result = await _controller.DeletePost(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    #endregion
}
