using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

public class PostsControllerTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<IPostScheduler> _schedulerMock;
    private readonly Mock<IFacebookInsightsService> _insightsMock;
    private readonly PostsController _controller;

    public PostsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _schedulerMock = new Mock<IPostScheduler>();
        _insightsMock = new Mock<IFacebookInsightsService>();

        _schedulerMock.Setup(x => x.ScheduleAsync(It.IsAny<PostPilot.Api.Entities.Post>()))
            .ReturnsAsync(new ScheduleResult(true, "test-arn", null));

        _controller = new PostsController(
            _context,
            _schedulerMock.Object,
            _insightsMock.Object,
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

    #region CreatePost Platform-Specific Validation Tests

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.LinkedIn, 3000)]
    [InlineData(Platform.Twitter, 280)]
    public async Task CreatePost_TextAtExactMaxLength_Succeeds(Platform platform, int maxLength)
    {
        var content = new string('x', maxLength);
        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: null,
            MediaType: null,
            Platform: platform,
            ScheduledAt: DateTime.UtcNow.AddHours(1));

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
        var content = new string('x', 2200);
        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: "https://example.com/image.jpg",
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id);

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

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
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
        var request = new CreatePostRequest(
            Content: content,
            MediaUrl: "https://example.com/image.jpg",
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.True(problemDetails.Errors.ContainsKey("content"));
        Assert.Contains("Text is too long for Instagram", problemDetails.Errors["content"][0]);
    }

    [Theory]
    [InlineData(Platform.Facebook)]
    [InlineData(Platform.LinkedIn)]
    [InlineData(Platform.Twitter)]
    public async Task CreatePost_NullContent_Succeeds(Platform platform)
    {
        // Posts can have null content (media-only posts)
        var request = new CreatePostRequest(
            Content: null!,
            MediaUrl: "https://example.com/image.jpg",
            MediaType: MediaType.Image,
            Platform: platform,
            ScheduledAt: DateTime.UtcNow.AddHours(1));

        var result = await _controller.CreatePost(request);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.NotNull(createdResult.Value);
    }

    #endregion

    #region Instagram-Specific Validation Tests

    [Fact]
    public async Task CreatePost_Instagram_RequiresTargetAccount()
    {
        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: "https://example.com/image.jpg",
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1));

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
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

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task CreatePost_Instagram_RejectsVideo()
    {
        var igAccount = await CreateTestInstagramAccount();

        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: "https://example.com/video.mp4",
            MediaType: MediaType.Video,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id);

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
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

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(400, objectResult.StatusCode);
    }

    [Fact]
    public async Task CreatePost_Instagram_WithImage_Succeeds()
    {
        var igAccount = await CreateTestInstagramAccount();

        var request = new CreatePostRequest(
            Content: "Test caption #hashtag",
            MediaUrl: "https://example.com/image.jpg",
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: igAccount.Id);

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
        var request = new CreatePostRequest(
            Content: "Test caption",
            MediaUrl: "https://example.com/image.jpg",
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: Guid.NewGuid()); // Non-existent account

        var result = await _controller.CreatePost(request);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
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
        // Create a post first
        var post = new PostPilot.Api.Entities.Post
        {
            Id = Guid.NewGuid(),
            Content = "Original content",
            Platform = platform,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            Status = PostStatus.Pending,
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
            ScheduledAt: DateTime.UtcNow.AddHours(1));

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
            Content = "Original content",
            Platform = platform,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            Status = PostStatus.Pending,
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

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);

        var problemDetails = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.True(problemDetails.Errors.ContainsKey("content"));
        Assert.Contains($"Text is too long for {platform}", problemDetails.Errors["content"][0]);
    }

    #endregion

    #region DeletePost Status-Based Rules Tests

    private Post CreateTestPost(PostStatus status)
    {
        return new Post
        {
            Id = Guid.NewGuid(),
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
    public async Task DeletePost_Pending_SetsCanceledStatusAndCallsSchedulerCancel()
    {
        var post = CreateTestPost(PostStatus.Pending);
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        Assert.IsType<NoContentResult>(result);

        var updated = await _context.Posts.FindAsync(post.Id);
        Assert.NotNull(updated);
        Assert.Equal(PostStatus.Canceled, updated.Status);
        Assert.NotNull(updated.CanceledAt);

        _schedulerMock.Verify(
            s => s.CancelScheduleAsync(It.Is<Post>(p => p.Id == post.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeletePost_RetryPending_SetsCanceledStatusAndCallsSchedulerCancel()
    {
        var post = CreateTestPost(PostStatus.RetryPending);
        post.NextRetryAt = DateTime.UtcNow.AddMinutes(5);
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        Assert.IsType<NoContentResult>(result);

        var updated = await _context.Posts.FindAsync(post.Id);
        Assert.NotNull(updated);
        Assert.Equal(PostStatus.Canceled, updated.Status);
        Assert.NotNull(updated.CanceledAt);

        _schedulerMock.Verify(
            s => s.CancelScheduleAsync(It.Is<Post>(p => p.Id == post.Id), It.IsAny<CancellationToken>()),
            Times.Once);
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
    public async Task DeletePost_Canceled_Returns204Idempotent()
    {
        var post = CreateTestPost(PostStatus.Canceled);
        post.CanceledAt = DateTime.UtcNow.AddMinutes(-5);
        _context.Posts.Add(post);
        await _context.SaveChangesAsync();

        var result = await _controller.DeletePost(post.Id);

        Assert.IsType<NoContentResult>(result);

        // Post should still exist (not deleted, just already canceled)
        var unchanged = await _context.Posts.FindAsync(post.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(PostStatus.Canceled, unchanged.Status);
    }

    [Fact]
    public async Task DeletePost_NotFound_Returns404()
    {
        var result = await _controller.DeletePost(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    #endregion
}
