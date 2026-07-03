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
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Safe media response headers on the authenticated <c>GET /api/media/{mediaId}/file</c> route
/// (<see cref="MediaController.GetMediaFile"/>): X-Content-Type-Options: nosniff, renderable
/// content types for known image/video (so previews work), application/octet-stream +
/// attachment for unknown/unsafe content types, and frame-filename traversal rejection on the
/// separate (still-anonymous, local-dev-only) frames route.
/// </summary>
public class MediaResponseHeaderTests
{
    private static (MediaController Controller, AppDbContext Db) NewController(AppRunMode runMode, Guid workspaceId)
    {
        var storage = new Mock<IMediaStorageProvider>();
        storage.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1, 2, 3, 4 }));

        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.StorageProvider).Returns(storage.Object);
        mediaService.Setup(m => m.RunMode).Returns(runMode);

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var workspace = new Mock<ICurrentWorkspaceProvider>();
        workspace.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceId);

        var controller = new MediaController(
            mediaService.Object,
            new Mock<IMediaUploadService>().Object,
            new Mock<IMediaValidationService>().Object,
            new Mock<IMediaValidationGate>().Object,
            workspace.Object,
            db,
            NullLogger<MediaController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, db);
    }

    private static Media SeedMedia(AppDbContext db, Guid workspaceId, string storageKey, string contentType)
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
        db.Media.Add(media);
        db.SaveChanges();
        return media;
    }

    [Fact]
    public async Task GetMediaFile_image_has_nosniff_and_renderable_type_no_attachment()
    {
        var workspaceId = Guid.NewGuid();
        var (controller, db) = NewController(AppRunMode.Server, workspaceId);
        var media = SeedMedia(db, workspaceId, "media/x.png", "image/png");

        var result = await controller.GetMediaFile(media.Id, null, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/png", file.ContentType); // renderable — previews keep working
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.False(controller.Response.Headers.ContainsKey("Content-Disposition"));
    }

    [Fact]
    public async Task GetMediaFile_video_is_renderable_with_nosniff()
    {
        var workspaceId = Guid.NewGuid();
        var (controller, db) = NewController(AppRunMode.Server, workspaceId);
        var media = SeedMedia(db, workspaceId, "media/x.mp4", "video/mp4");

        var result = await controller.GetMediaFile(media.Id, null, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("video/mp4", file.ContentType);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.False(controller.Response.Headers.ContainsKey("Content-Disposition"));
    }

    [Fact]
    public async Task GetMediaFile_unknown_content_type_falls_back_to_octet_stream_and_attachment()
    {
        var workspaceId = Guid.NewGuid();
        var (controller, db) = NewController(AppRunMode.Server, workspaceId);
        var media = SeedMedia(db, workspaceId, "media/x.weird", "application/x-weird");

        var result = await controller.GetMediaFile(media.Id, null, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("attachment", controller.Response.Headers["Content-Disposition"].ToString());
    }

    [Fact]
    public async Task GetMediaFile_unknown_mediaId_returns_404()
    {
        var workspaceId = Guid.NewGuid();
        var (controller, _) = NewController(AppRunMode.Server, workspaceId);

        var result = await controller.GetMediaFile(Guid.NewGuid(), null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task GetMediaFile_foreign_workspace_mediaId_returns_404()
    {
        var workspaceId = Guid.NewGuid();
        var foreignWorkspaceId = Guid.NewGuid();
        var (controller, db) = NewController(AppRunMode.Server, workspaceId);
        var media = SeedMedia(db, foreignWorkspaceId, "media/foreign.png", "image/png");

        var result = await controller.GetMediaFile(media.Id, null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..%2f..%2fsecret")]
    [InlineData("subdir/../../escape.jpg")]
    public async Task GetFrame_traversal_is_rejected(string filename)
    {
        var (controller, _) = NewController(AppRunMode.Local, Guid.NewGuid());

        var result = controller.GetFrame(filename);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
