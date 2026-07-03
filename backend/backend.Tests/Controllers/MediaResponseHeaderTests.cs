using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Safe media response headers: X-Content-Type-Options: nosniff on served files, renderable
/// content types for known image/video (so previews work), application/octet-stream +
/// attachment for unknown extensions, and frame-filename traversal rejection.
/// </summary>
public class MediaResponseHeaderTests
{
    private static MediaController NewController(AppRunMode runMode)
    {
        var storage = new Mock<IMediaStorageProvider>();
        storage.Setup(s => s.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1, 2, 3, 4 }));

        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.StorageProvider).Returns(storage.Object);
        mediaService.Setup(m => m.RunMode).Returns(runMode);

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        return new MediaController(
            mediaService.Object,
            new Mock<IMediaUploadService>().Object,
            new Mock<IMediaValidationService>().Object,
            new Mock<IMediaValidationGate>().Object,
            new Mock<ICurrentWorkspaceProvider>().Object,
            db,
            NullLogger<MediaController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    [Fact]
    public async Task GetFile_image_has_nosniff_and_renderable_type_no_attachment()
    {
        var controller = NewController(AppRunMode.Server);

        var result = await controller.GetFile("media/x.png", CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/png", file.ContentType); // renderable — previews keep working
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.False(controller.Response.Headers.ContainsKey("Content-Disposition"));
    }

    [Fact]
    public async Task GetFile_video_is_renderable_with_nosniff()
    {
        var controller = NewController(AppRunMode.Server);

        var result = await controller.GetFile("media/x.mp4", CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("video/mp4", file.ContentType);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.False(controller.Response.Headers.ContainsKey("Content-Disposition"));
    }

    [Fact]
    public async Task GetFile_unknown_extension_falls_back_to_octet_stream_and_attachment()
    {
        var controller = NewController(AppRunMode.Server);

        var result = await controller.GetFile("media/x.weird", CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/octet-stream", file.ContentType);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.Equal("attachment", controller.Response.Headers["Content-Disposition"].ToString());
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..%2f..%2fsecret")]
    [InlineData("subdir/../../escape.jpg")]
    public async Task GetFrame_traversal_is_rejected(string filename)
    {
        var controller = NewController(AppRunMode.Local);

        var result = controller.GetFrame(filename);

        Assert.IsType<NotFoundObjectResult>(result);
    }
}
