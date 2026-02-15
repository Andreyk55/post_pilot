using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Integration tests for cancel and delete endpoints on PostsController.
/// Uses EF Core InMemory provider for a real DbContext.
/// </summary>
public class PostCancelDeleteTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly PostsController _controller;
    private readonly Mock<IPostScheduler> _schedulerMock;

    public PostCancelDeleteTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _schedulerMock = new Mock<IPostScheduler>();

        _schedulerMock
            .Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var loggerMock = new Mock<ILogger<PostsController>>();
        var insightsMock = new Mock<IFacebookInsightsService>();

        _controller = new PostsController(_dbContext, _schedulerMock.Object, insightsMock.Object, loggerMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    private Post CreatePost(PostStatus status)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Test post",
            Platform = Platform.Facebook,
            Status = status,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _dbContext.Posts.Add(post);
        _dbContext.SaveChanges();
        return post;
    }

    // ──────────────────────────────────────────────
    //  CANCEL TESTS
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Cancel_Scheduled_Returns200_And_StatusBecomesCanceled()
    {
        var post = CreatePost(PostStatus.Scheduled);

        var result = await _controller.CancelPost(post.Id);

        Assert.IsType<OkResult>(result);

        var updated = await _dbContext.Posts.FindAsync(post.Id);
        Assert.Equal(PostStatus.Canceled, updated!.Status);
        Assert.NotNull(updated.CanceledAt);
    }

    [Fact]
    public async Task Cancel_RetryPending_Returns200_And_StatusBecomesCanceled()
    {
        var post = CreatePost(PostStatus.RetryPending);

        var result = await _controller.CancelPost(post.Id);

        Assert.IsType<OkResult>(result);

        var updated = await _dbContext.Posts.FindAsync(post.Id);
        Assert.Equal(PostStatus.Canceled, updated!.Status);
    }

    [Fact]
    public async Task Cancel_AlreadyCanceled_Returns200_Idempotent()
    {
        var post = CreatePost(PostStatus.Canceled);

        var result = await _controller.CancelPost(post.Id);

        Assert.IsType<OkResult>(result);
    }

    [Fact]
    public async Task Cancel_Failed_Returns200_And_StatusBecomesCanceled()
    {
        var post = CreatePost(PostStatus.Failed);

        var result = await _controller.CancelPost(post.Id);

        Assert.IsType<OkResult>(result);

        var updated = await _dbContext.Posts.FindAsync(post.Id);
        Assert.Equal(PostStatus.Canceled, updated!.Status);
    }

    [Fact]
    public async Task Cancel_Published_Returns409()
    {
        var post = CreatePost(PostStatus.Published);

        var result = await _controller.CancelPost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Cancel_Publishing_Returns409()
    {
        var post = CreatePost(PostStatus.Publishing);

        var result = await _controller.CancelPost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Cancel_NotFound_Returns404()
    {
        var result = await _controller.CancelPost(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Cancel_Scheduled_CancelsSchedule()
    {
        var post = CreatePost(PostStatus.Scheduled);

        await _controller.CancelPost(post.Id);

        _schedulerMock.Verify(
            s => s.CancelScheduleAsync(It.Is<Post>(p => p.Id == post.Id), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ──────────────────────────────────────────────
    //  DELETE TESTS
    // ──────────────────────────────────────────────

    [Fact]
    public async Task Delete_Canceled_Returns204_And_RecordRemoved()
    {
        var post = CreatePost(PostStatus.Canceled);

        var result = await _controller.DeletePost(post.Id);

        Assert.IsType<NoContentResult>(result);

        var deleted = await _dbContext.Posts.FindAsync(post.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task Delete_Failed_Returns204_And_RecordRemoved()
    {
        var post = CreatePost(PostStatus.Failed);

        var result = await _controller.DeletePost(post.Id);

        Assert.IsType<NoContentResult>(result);

        var deleted = await _dbContext.Posts.FindAsync(post.Id);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task Delete_Scheduled_Returns409()
    {
        var post = CreatePost(PostStatus.Scheduled);

        var result = await _controller.DeletePost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        // Record should still exist
        var existing = await _dbContext.Posts.FindAsync(post.Id);
        Assert.NotNull(existing);
    }

    [Fact]
    public async Task Delete_Published_Returns409()
    {
        var post = CreatePost(PostStatus.Published);

        var result = await _controller.DeletePost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Delete_Publishing_Returns409()
    {
        var post = CreatePost(PostStatus.Publishing);

        var result = await _controller.DeletePost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Delete_RetryPending_Returns409()
    {
        var post = CreatePost(PostStatus.RetryPending);

        var result = await _controller.DeletePost(post.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Delete_NotFound_Returns404()
    {
        var result = await _controller.DeletePost(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }
}
