using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

public class PostThumbnailDtoTests
{
    [Fact]
    public async Task GetPost_And_GetPostDetails_IncludeVideoThumbnailMetadata()
    {
        var workspaceId = Guid.NewGuid();
        var postId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        const string storageKey = "users/u/workspaces/w/providers/meta-facebook/media/m/clip.mp4";
        const string thumbnailKey = "users/u/workspaces/w/providers/meta-facebook/media/m/thumbnail.jpg";

        await using var db = NewDb();
        db.Posts.Add(new Post
        {
            Id = postId,
            WorkspaceId = workspaceId,
            Content = "Video post",
            MediaUrl = storageKey,
            MediaType = MediaType.Video,
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        db.Media.Add(new Media
        {
            Id = mediaId,
            WorkspaceId = workspaceId,
            StorageProvider = "supabase",
            Bucket = "postpilot-media",
            StorageKey = storageKey,
            OriginalFileName = "clip.mp4",
            ContentType = "video/mp4",
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
            ThumbnailStorageKey = thumbnailKey,
            ThumbnailMimeType = "image/jpeg",
            ThumbnailWidth = 480,
            ThumbnailHeight = 270,
            ThumbnailSizeBytes = 12345,
            ThumbnailCreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var workspaceMock = new Mock<ICurrentWorkspaceProvider>();
        workspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceId);

        var controller = new PostsController(
            db,
            new Mock<IPostScheduler>().Object,
            new Mock<IFacebookInsightsService>().Object,
            workspaceMock.Object,
            new PassThroughMediaGate(),
            NullLogger<PostsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        controller.ControllerContext.HttpContext.Request.Scheme = "https";
        controller.ControllerContext.HttpContext.Request.Host = new HostString("api.example.test");

        var getPost = await controller.GetPost(postId);
        var postDto = Assert.IsType<PostDto>(getPost.Value);
        Assert.NotNull(postDto.Thumbnail);
        Assert.Equal(thumbnailKey, postDto.Thumbnail!.StorageKey);
        Assert.Equal("https://api.example.test/api/media/files/users/u/workspaces/w/providers/meta-facebook/media/m/thumbnail.jpg", postDto.Thumbnail.Url);
        Assert.Equal("image/jpeg", postDto.Thumbnail.MimeType);
        Assert.Equal(480, postDto.Thumbnail.Width);
        Assert.Equal(270, postDto.Thumbnail.Height);

        var details = await controller.GetPostDetails(postId, CancellationToken.None);
        var detailsDto = Assert.IsType<PostDetailsDto>(details.Value);
        Assert.NotNull(detailsDto.Thumbnail);
        Assert.Equal(thumbnailKey, detailsDto.Thumbnail!.StorageKey);
        Assert.Equal("https://api.example.test/api/media/files/users/u/workspaces/w/providers/meta-facebook/media/m/thumbnail.jpg", detailsDto.Thumbnail.Url);
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}