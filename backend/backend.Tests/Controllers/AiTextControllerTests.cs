using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Auth;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

public class AiTextControllerTests
{
    private readonly Mock<IGeminiClient> _geminiClientMock;
    private readonly Mock<IAiRateLimiter> _rateLimiterMock;
    private readonly Mock<ICurrentUserProvider> _currentUserMock;
    private readonly Mock<ICurrentWorkspaceProvider> _currentWorkspaceMock;
    private readonly AiTextController _controller;

    public AiTextControllerTests()
    {
        _geminiClientMock = new Mock<IGeminiClient>();
        _rateLimiterMock = new Mock<IAiRateLimiter>();
        _currentUserMock = new Mock<ICurrentUserProvider>();
        _currentWorkspaceMock = new Mock<ICurrentWorkspaceProvider>();

        _currentUserMock.Setup(x => x.GetCurrentUserId())
            .Returns(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        _currentWorkspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.Parse("00000000-0000-0000-0000-0000000000aa"));

        _controller = new AiTextController(
            _geminiClientMock.Object,
            _rateLimiterMock.Object,
            null!,
            null!,
            null!,
            null!,
            _currentUserMock.Object,
            _currentWorkspaceMock.Object,
            NullLogger<AiTextController>.Instance);
    }

    [Fact]
    public async Task ProcessText_EmptyText_ReturnsBadRequest()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var request = new AiTextRequest(AiTextAction.Polish, AiPlatform.Facebook, "", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProcessText_TextTooLong_ReturnsBadRequest()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var longText = new string('x', 5001); // Over 5000 limit
        var request = new AiTextRequest(AiTextAction.Polish, AiPlatform.Facebook, longText, null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProcessText_RewriteToneWithoutTone_ReturnsBadRequest()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var request = new AiTextRequest(AiTextAction.RewriteTone, AiPlatform.Facebook, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProcessText_RateLimitExceeded_Returns429()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _rateLimiterMock.Setup(x => x.GetRemainingCallsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var request = new AiTextRequest(AiTextAction.Polish, AiPlatform.Facebook, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProcessText_ValidPolishRequest_ReturnsVariants()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var expectedResponse = new AiTextVariantsResponse(
            AiTextAction.Polish,
            new List<AiTextVariant>
            {
                new("Option 1", "Polished text 1"),
                new("Option 2", "Polished text 2"),
                new("Option 3", "Polished text 3")
            });

        _geminiClientMock.Setup(x => x.GenerateVariantsAsync(
                AiTextAction.Polish,
                AiPlatform.Facebook,
                "Test text",
                null,
                "en",
                It.IsAny<AiVoiceProfile?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var request = new AiTextRequest(AiTextAction.Polish, AiPlatform.Facebook, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiTextVariantsResponse>(okResult.Value);
        Assert.Equal(3, response.Variants.Count);
    }

    [Fact]
    public async Task ProcessText_ValidHashtagsRequest_ReturnsHashtags()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var expectedResponse = new AiHashtagsResponse(
            AiTextAction.Hashtags,
            new List<string> { "#social", "#media", "#marketing" });

        _geminiClientMock.Setup(x => x.GenerateHashtagsAsync(
                AiPlatform.Instagram,
                "Test text",
                "en",
                It.IsAny<AiVoiceProfile?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var request = new AiTextRequest(AiTextAction.Hashtags, AiPlatform.Instagram, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiHashtagsResponse>(okResult.Value);
        Assert.Equal(3, response.Hashtags.Count);
    }

    [Fact]
    public async Task ProcessText_ValidPreFlightRequest_ReturnsScoreAndIssues()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var expectedResponse = new AiPreFlightResponse(
            AiTextAction.PreFlight,
            85,
            new List<AiPreFlightIssue>
            {
                new(AiIssueSeverity.Info, "Consider adding hashtags", "#social")
            });

        _geminiClientMock.Setup(x => x.RunPreFlightCheckAsync(
                AiPlatform.X,
                "Test text",
                "en",
                It.IsAny<AiVoiceProfile?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var request = new AiTextRequest(AiTextAction.PreFlight, AiPlatform.X, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiPreFlightResponse>(okResult.Value);
        Assert.Equal(85, response.Score);
        Assert.Single(response.Issues);
    }

    [Fact]
    public async Task ProcessText_GeminiApiQuotaExceeded_Returns429()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _geminiClientMock.Setup(x => x.GenerateVariantsAsync(
                It.IsAny<AiTextAction>(),
                It.IsAny<AiPlatform>(),
                It.IsAny<string>(),
                It.IsAny<AiTone?>(),
                It.IsAny<string>(),
                It.IsAny<AiVoiceProfile?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GeminiApiException("Quota exceeded", 429));

        var request = new AiTextRequest(AiTextAction.Polish, AiPlatform.Facebook, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProcessText_GeminiApiTimeout_Returns504()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _geminiClientMock.Setup(x => x.GenerateVariantsAsync(
                It.IsAny<AiTextAction>(),
                It.IsAny<AiPlatform>(),
                It.IsAny<string>(),
                It.IsAny<AiTone?>(),
                It.IsAny<string>(),
                It.IsAny<AiVoiceProfile?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GeminiApiException("Request timed out", 504));

        var request = new AiTextRequest(AiTextAction.Polish, AiPlatform.Facebook, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, objectResult.StatusCode);
    }

    [Fact]
    public async Task ProcessText_GeminiApiUnavailable_Returns503()
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _geminiClientMock.Setup(x => x.GenerateVariantsAsync(
                It.IsAny<AiTextAction>(),
                It.IsAny<AiPlatform>(),
                It.IsAny<string>(),
                It.IsAny<AiTone?>(),
                It.IsAny<string>(),
                It.IsAny<AiVoiceProfile?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GeminiApiException("Service unavailable", 503));

        var request = new AiTextRequest(AiTextAction.Polish, AiPlatform.Facebook, "Test text", null, "en");

        var result = await _controller.ProcessText(request, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);
    }
}
