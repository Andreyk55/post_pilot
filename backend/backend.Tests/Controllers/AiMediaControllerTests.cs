using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Media;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

public class AiMediaControllerTests
{
    private readonly Mock<IMediaAiService> _mediaAiServiceMock;
    private readonly Mock<IAiRateLimiter> _rateLimiterMock;
    private readonly Mock<ICurrentUserProvider> _currentUserMock;
    private readonly Mock<ICurrentWorkspaceProvider> _currentWorkspaceMock;
    private readonly AppDbContext _db;
    private readonly AiMediaController _controller;
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid OtherWorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    public AiMediaControllerTests()
    {
        _mediaAiServiceMock = new Mock<IMediaAiService>();
        _rateLimiterMock = new Mock<IAiRateLimiter>();
        _currentUserMock = new Mock<ICurrentUserProvider>();
        _currentWorkspaceMock = new Mock<ICurrentWorkspaceProvider>();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        _currentUserMock.Setup(x => x.GetCurrentUserId())
            .Returns(UserId);
        _currentWorkspaceMock.Setup(x => x.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WorkspaceId);

        // Mirrors the real MediaService.IsStorageKey contract: external URLs (http/https/file/
        // ftp/...) and bare hosts/IPs are NOT storage keys; only server-issued keys under
        // media/ | workspaces/ | users/ are. The ownership service then re-checks the DB row.
        var mediaServiceMock = new Mock<IMediaService>();
        mediaServiceMock.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s =>
                !string.IsNullOrWhiteSpace(s)
                && !s.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && (s.StartsWith("media/", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("workspaces/", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("users/", StringComparison.OrdinalIgnoreCase)));

        var ownership = new MediaOwnershipService(_db, mediaServiceMock.Object);

        _controller = new AiMediaController(
            _mediaAiServiceMock.Object,
            _rateLimiterMock.Object,
            _db,
            ownership,
            _currentUserMock.Object,
            _currentWorkspaceMock.Object,
            NullLogger<AiMediaController>.Instance);
    }

    /// <summary>Inserts an Uploaded Media row so the given key is owned by <paramref name="workspaceId"/>.</summary>
    private void SeedMedia(string storageKey, Guid workspaceId)
    {
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StorageProvider = "local-disk",
            Bucket = "",
            StorageKey = storageKey,
            OriginalFileName = "img.jpg",
            ContentType = "image/jpeg",
            SizeBytes = 1024,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task ProcessMedia_ImageCaptionIdeas_StillDispatchesToImageService()
    {
        SeedMedia("media/image.jpg", WorkspaceId);
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var expected = new AiMediaCaptionIdeasResponse(
            AiMediaAction.CaptionIdeas,
            new List<AiMediaCaptionVariant> { new("Option 1", "Image caption") });

        _mediaAiServiceMock
            .Setup(x => x.GenerateImageCaptionIdeasAsync("media/image.jpg", AiPlatform.Facebook, "hello", "en", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.CaptionIdeas,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new("media/image.jpg", "image") },
                "hello"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaCaptionIdeasResponse>(ok.Value);
        Assert.Single(response.Variants);

        _mediaAiServiceMock.Verify(
            x => x.GenerateImageCaptionIdeasAsync("media/image.jpg", AiPlatform.Facebook, "hello", "en", null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_ImageCaptionIdeas_LoadsVoiceProfileFromCurrentWorkspace()
    {
        SeedMedia("media/image.jpg", WorkspaceId);
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var profile = new AiVoiceProfile
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000123"),
            WorkspaceId = WorkspaceId,
            UserId = UserId,
            Name = "Brand",
            Description = "Helpful voice",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.AiVoiceProfiles.Add(profile);
        await _db.SaveChangesAsync();

        var expected = new AiMediaCaptionIdeasResponse(
            AiMediaAction.CaptionIdeas,
            new List<AiMediaCaptionVariant> { new("Option 1", "Image caption") });

        _mediaAiServiceMock
            .Setup(x => x.GenerateImageCaptionIdeasAsync(
                "media/image.jpg",
                AiPlatform.Facebook,
                "hello",
                "en",
                It.Is<AiVoiceProfile?>(p => p != null && p.Id == profile.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.CaptionIdeas,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new("media/image.jpg", "image") },
                "hello",
                VoiceProfileId: profile.Id),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<AiMediaCaptionIdeasResponse>(ok.Value);

        _mediaAiServiceMock.Verify(
            x => x.GenerateImageCaptionIdeasAsync(
                "media/image.jpg",
                AiPlatform.Facebook,
                "hello",
                "en",
                It.Is<AiVoiceProfile?>(p => p != null && p.Id == profile.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_VideoCaptionIdeas_ReturnsEmptyResponseWithoutCallingService()
    {
        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.VideoCaptionIdeas,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new("media/video.mp4", "video") },
                "hello"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaCaptionIdeasResponse>(ok.Value);
        Assert.Empty(response.Variants);

        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaAiServiceMock.Verify(
            x => x.GenerateVideoCaptionIdeasAsync(It.IsAny<string>(), It.IsAny<AiPlatform>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<AiVoiceProfile?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_VideoImageAction_ReturnsUnsupportedResponseWithoutCallingService()
    {
        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.CaptionIdeas,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new("media/video.mp4", "video") },
                "hello"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaUnsupportedResponse>(ok.Value);
        Assert.Equal("Media AI supports a single image only.", response.Message);

        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaAiServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessMedia_MultipleImages_ReturnsUnsupportedResponseWithoutCallingService()
    {
        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.AltText,
                AiPlatform.Facebook,
                new List<AiMediaItemReference>
                {
                    new("media/image-1.jpg", "image", 0),
                    new("media/image-2.jpg", "image", 1),
                }),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaUnsupportedResponse>(ok.Value);
        Assert.Equal("Media AI supports a single image only.", response.Message);

        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaAiServiceMock.Verify(
            x => x.GenerateAltTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mediaAiServiceMock.Verify(
            x => x.GenerateImageCaptionIdeasAsync(It.IsAny<string>(), It.IsAny<AiPlatform>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<AiVoiceProfile?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_NoMedia_ReturnsValidationProblemWithoutCallingService()
    {
        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.ImageQualityCheck,
                AiPlatform.Facebook,
                new List<AiMediaItemReference>()),
            CancellationToken.None);

        var objectResult = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var details = Assert.IsType<ValidationProblemDetails>(objectResult.Value);
        Assert.Contains("mediaItems", details.Errors.Keys);

        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaAiServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessMedia_MixedMedia_ReturnsUnsupportedResponseWithoutCallingService()
    {
        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.CaptionIdeas,
                AiPlatform.Facebook,
                new List<AiMediaItemReference>
                {
                    new("media/image.jpg", "image", 0),
                    new("media/video.mp4", "video", 1),
                },
                "hello"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<AiMediaUnsupportedResponse>(ok.Value);
        Assert.Equal("Media AI supports a single image only.", response.Message);

        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mediaAiServiceMock.VerifyNoOtherCalls();
    }

    // ── H1: SSRF / cross-workspace ownership gate on AssetUrl ───────────────────

    [Theory]
    [InlineData("http://127.0.0.1:5122/health")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://example.com/image.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://internal/host")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    public async Task ProcessMedia_ExternalOrNonStorageAssetUrl_IsRejectedWithoutFetching(string assetUrl)
    {
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.AltText,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new(assetUrl, "image") }),
            CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFound.StatusCode);

        // Nothing was fetched or dispatched, and the rate limiter was never consumed.
        _mediaAiServiceMock.VerifyNoOtherCalls();
        _rateLimiterMock.Verify(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_ForeignWorkspaceStorageKey_IsRejected()
    {
        // Key exists, but belongs to a DIFFERENT workspace than the caller's.
        SeedMedia("media/foreign.jpg", OtherWorkspaceId);

        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.AltText,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new("media/foreign.jpg", "image") }),
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        _mediaAiServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessMedia_UnknownStorageKey_IsRejected()
    {
        // Well-formed key shape but no Media row anywhere.
        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.AltText,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new("media/does-not-exist.jpg", "image") }),
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        _mediaAiServiceMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessMedia_OwnedStorageKey_ReachesAiProcessing()
    {
        SeedMedia("users/abc/workspaces/x/providers/meta-facebook/media/y/photo.jpg", WorkspaceId);
        _rateLimiterMock.Setup(x => x.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var expected = new AiAltTextResponse(AiMediaAction.AltText, "A photo");
        _mediaAiServiceMock
            .Setup(x => x.GenerateAltTextAsync(
                "users/abc/workspaces/x/providers/meta-facebook/media/y/photo.jpg",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _controller.ProcessMedia(
            new AiMediaRequest(
                AiMediaAction.AltText,
                AiPlatform.Facebook,
                new List<AiMediaItemReference> { new("users/abc/workspaces/x/providers/meta-facebook/media/y/photo.jpg", "image") }),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<AiAltTextResponse>(ok.Value);
        _mediaAiServiceMock.Verify(
            x => x.GenerateAltTextAsync(
                "users/abc/workspaces/x/providers/meta-facebook/media/y/photo.jpg",
                It.IsAny<CancellationToken>()),
            Times.Once);
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
            x => x.GenerateVideoCaptionIdeasFromFrameAsync(It.IsAny<string>(), It.IsAny<AiPlatform>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<AiVoiceProfile?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
