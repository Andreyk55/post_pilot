using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Auth;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

public class AiMediaControllerTests
{
    private readonly Mock<IMediaAiService> _mediaAiServiceMock;
    private readonly Mock<IAiRateLimiter> _rateLimiterMock;
    private readonly Mock<ICurrentUserProvider> _currentUserMock;
    private readonly AiMediaController _controller;

    public AiMediaControllerTests()
    {
        _mediaAiServiceMock = new Mock<IMediaAiService>();
        _rateLimiterMock = new Mock<IAiRateLimiter>();
        _currentUserMock = new Mock<ICurrentUserProvider>();

        _currentUserMock.Setup(x => x.GetCurrentUserId())
            .Returns(Guid.Parse("00000000-0000-0000-0000-000000000001"));

        _controller = new AiMediaController(
            _mediaAiServiceMock.Object,
            _rateLimiterMock.Object,
            _currentUserMock.Object,
            NullLogger<AiMediaController>.Instance);
    }

    [Fact]
    public async Task ProcessMedia_ImageCaptionIdeas_StillDispatchesToImageService()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var expected = new AiMediaCaptionIdeasResponse(
            AiMediaAction.CaptionIdeas,
            new List<AiMediaCaptionVariant> { new("Option 1", "Image caption") });

        _mediaAiServiceMock
            .Setup(x => x.GenerateImageCaptionIdeasAsync("media/image.jpg", AiPlatform.Facebook, "hello", "en", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _controller.ProcessMedia(
            new AiMediaRequest(AiMediaAction.CaptionIdeas, AiPlatform.Facebook, "media/image.jpg", "image", "hello"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaCaptionIdeasResponse>(ok.Value);
        Assert.Single(response.Variants);

        _mediaAiServiceMock.Verify(
            x => x.GenerateImageCaptionIdeasAsync("media/image.jpg", AiPlatform.Facebook, "hello", "en", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_VideoCaptionIdeas_ReturnsEmptyResponseWithoutCallingService()
    {
        var result = await _controller.ProcessMedia(
            new AiMediaRequest(AiMediaAction.VideoCaptionIdeas, AiPlatform.Facebook, "media/video.mp4", "video", "hello"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaCaptionIdeasResponse>(ok.Value);
        Assert.Empty(response.Variants);

        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaAiServiceMock.Verify(
            x => x.GenerateVideoCaptionIdeasAsync(It.IsAny<string>(), It.IsAny<AiPlatform>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessThumbnailFrames_ReturnsEmptyResponseWithoutCallingService()
    {
        var result = await _controller.ProcessThumbnailFrames(
            new AiThumbnailFramesRequest(new List<ClientExtractedFrame>()),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiThumbnailSuggestResponse>(ok.Value);
        Assert.Empty(response.Frames);

        _mediaAiServiceMock.Verify(
            x => x.ProcessClientExtractedFramesAsync(It.IsAny<List<ClientExtractedFrame>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessVideoCaptionIdeas_ReturnsEmptyResponseWithoutCallingService()
    {
        var result = await _controller.ProcessVideoCaptionIdeas(
            new AiVideoCaptionIdeasRequest(AiPlatform.Facebook, "data:image/jpeg;base64,abc", "hello"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaCaptionIdeasResponse>(ok.Value);
        Assert.Empty(response.Variants);

        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaAiServiceMock.Verify(
            x => x.GenerateVideoCaptionIdeasFromFrameAsync(It.IsAny<string>(), It.IsAny<AiPlatform>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}