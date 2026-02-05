using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
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

    #region CreatePost Platform-Specific Validation Tests

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.Instagram, 2200)]
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

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.Instagram, 2200)]
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

    [Theory]
    [InlineData(Platform.Facebook)]
    [InlineData(Platform.Instagram)]
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

    #region UpdatePost Platform-Specific Validation Tests

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.Instagram, 2200)]
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
    [InlineData(Platform.Instagram, 2200)]
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
}
