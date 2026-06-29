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

public class MediaControllerQuotaTests
{
    [Fact]
    public async Task InitUpload_WhenQuotaExceeded_Returns429ProblemDetails()
    {
        var quotaResult = new MediaUploadQuotaResult(
            Allowed: false,
            Limit: 100,
            Used: 100,
            Remaining: 0,
            PeriodEndUtc: new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc),
            ErrorCode: MediaUploadQuotaExceededException.DefaultErrorCode);

        var uploadService = new Mock<IMediaUploadService>();
        uploadService
            .Setup(x => x.InitAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<Platform>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new MediaUploadQuotaExceededException(quotaResult));

        var currentWorkspace = new Mock<ICurrentWorkspaceProvider>();
        currentWorkspace
            .Setup(x => x.GetCurrentWorkspaceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentWorkspaceInfo(Guid.NewGuid(), Guid.NewGuid(), "Workspace"));

        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        var controller = new MediaController(
            new Mock<IMediaService>().Object,
            uploadService.Object,
            new Mock<IMediaValidationService>().Object,
            new Mock<IMediaValidationGate>().Object,
            currentWorkspace.Object,
            db,
            NullLogger<MediaController>.Instance);

        var result = await controller.InitUpload(
            new InitUploadRequest("photo.png", "image/png", 100, Platform.Facebook),
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(429, objectResult.StatusCode);

        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Media upload quota exceeded", problem.Title);
        Assert.Equal("Daily media upload limit reached. You can upload more media when your quota resets.", problem.Detail);
        Assert.Equal(MediaUploadQuotaExceededException.DefaultErrorCode, problem.Extensions["code"]);
        Assert.Equal(100, problem.Extensions["limit"]);
        Assert.Equal(100, problem.Extensions["used"]);
        Assert.Equal(0, problem.Extensions["remaining"]);
        Assert.Equal(new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc), problem.Extensions["resetAtUtc"]);
    }
}
