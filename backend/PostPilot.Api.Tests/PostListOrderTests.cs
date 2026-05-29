using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests;

public class PostListOrderTests : IDisposable
{
    private static readonly Guid TestWorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

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
        var workspaceMock = new Mock<ICurrentWorkspaceProvider>();
        workspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(TestWorkspaceId);

        _controller = new PostsController(_dbContext, schedulerMock.Object, insightsMock.Object, workspaceMock.Object, loggerMock.Object);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task GetPosts_ReturnsPostsOrderedByCreatedAtDescending()
    {
        // Arrange: 3 posts with different CreatedAt, ScheduledAt deliberately mismatched.
        // GetPosts filters by TargetPage.MetaConnection.IsConnected, so we need a real
        // MetaConnection + ConnectedPage row that the posts can FK to.
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
        _dbContext.MetaConnections.Add(connection);
        _dbContext.ConnectedPages.Add(page);
        await _dbContext.SaveChangesAsync();
        var pageId = page.Id;

        var oldest = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = "Oldest created",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            TargetPageId = pageId,
            CreatedAt = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            ScheduledAt = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), // scheduled latest
            UpdatedAt = DateTime.UtcNow
        };

        var middle = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = "Middle created",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            TargetPageId = pageId,
            CreatedAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc),
            ScheduledAt = new DateTime(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc), // scheduled earliest
            UpdatedAt = DateTime.UtcNow
        };

        var newest = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = TestWorkspaceId,
            Content = "Newest created",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Scheduled,
            TargetPageId = pageId,
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
