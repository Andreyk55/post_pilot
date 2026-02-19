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

public class PostListOrderTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly PostsController _controller;

    public PostListOrderTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);

        var schedulerMock = new Mock<IPostScheduler>();
        var loggerMock = new Mock<ILogger<PostsController>>();
        var insightsMock = new Mock<IFacebookInsightsService>();

        _controller = new PostsController(_dbContext, schedulerMock.Object, insightsMock.Object, loggerMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task GetPosts_ReturnsPostsOrderedByCreatedAtDescending()
    {
        // Arrange: 3 posts with different CreatedAt, ScheduledAt deliberately mismatched
        var oldest = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Oldest created",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            CreatedAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            ScheduledAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), // scheduled latest
            UpdatedAt = DateTime.UtcNow
        };

        var middle = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Middle created",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            CreatedAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            ScheduledAt = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc), // scheduled earliest
            UpdatedAt = DateTime.UtcNow
        };

        var newest = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Newest created",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            CreatedAt = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc),
            ScheduledAt = new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc),
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Posts.AddRange(oldest, middle, newest);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _controller.GetPosts(page: 1, pageSize: 10);

        // Assert
        var response = result.Value;
        Assert.NotNull(response);
        Assert.Equal(3, response!.Items.Count);

        // Should be ordered by CreatedAt DESC: newest, middle, oldest
        Assert.Equal("Newest created", response.Items[0].Content);
        Assert.Equal("Middle created", response.Items[1].Content);
        Assert.Equal("Oldest created", response.Items[2].Content);
    }
}
