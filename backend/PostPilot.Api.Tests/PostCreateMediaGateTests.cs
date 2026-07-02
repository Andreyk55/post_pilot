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
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Integration tests for the authoritative media gate inside <see cref="PostsController.CreatePost"/>.
/// Wires the REAL <see cref="MediaValidationGate"/> so a request that the SPA would block, but a
/// crafted client could replay, is rejected server-side. Storage is faked so keys resolve to
/// generated image files; everything else (DB, controller validation) is real.
/// </summary>
public class PostCreateMediaGateTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000c4");

    private readonly AppDbContext _db;
    private PostsController _controller;
    private readonly Dictionary<string, string> _keyToPath = new();
    private readonly List<string> _tempFiles = new();

    public PostCreateMediaGateTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        // Default: no real video metadata (image tests never touch the video extractor).
        _controller = BuildController(Mock.Of<IVideoMetadataExtractor>());
    }

    /// <summary>
    /// Builds the controller with the REAL media gate over faked storage. Tests that exercise
    /// video rules pass a fake <see cref="IVideoMetadataExtractor"/> so the gate sees controlled
    /// dimensions/duration/codec; the default uses a no-op extractor for image-only tests.
    /// </summary>
    private PostsController BuildController(IVideoMetadataExtractor videoExtractor)
    {
        var scheduler = new Mock<IPostScheduler>();
        scheduler.Setup(s => s.ScheduleAsync(It.IsAny<Post>())).ReturnsAsync(new ScheduleResult(true, "arn", null));

        var workspace = new Mock<ICurrentWorkspaceProvider>();
        workspace.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Ws);

        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>())).Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>()))
            .Returns<string>(key => Task.FromResult<string?>(_keyToPath.TryGetValue(key, out var p) ? p : null));
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>()));

        var gate = new MediaValidationGate(
            _db, mediaService.Object,
            new MediaValidationService(
                new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
                videoExtractor,
                NullLogger<MediaValidationService>.Instance),
            NullLogger<MediaValidationGate>.Instance);

        return new PostsController(
            _db, scheduler.Object, Mock.Of<IFacebookInsightsService>(),
            workspace.Object, gate, NullLogger<PostsController>.Instance);
    }

    /// <summary>Rebuilds the controller so the gate sees the given video metadata for any path.</summary>
    private void UseVideoMetadata(int width, int height, double durationSeconds,
        string container = "mp4", string videoCodec = "h264", string audioCodec = "aac", double? fps = 30)
    {
        var meta = new VideoMetadata(width, height, durationSeconds, container, videoCodec, audioCodec,
            fps, null, container == "mov" ? "video/quicktime" : "video/mp4");
        var ext = new Mock<IVideoMetadataExtractor>();
        ext.Setup(e => e.ExtractAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _controller = BuildController(ext.Object);
    }

    /// <summary>Seeds a video Media row (placeholder bytes; metadata comes from the fake extractor).</summary>
    private string SeedVideoMedia(string storageKey, string contentType, long sizeBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"createtest_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[] { 0x00 });
        _tempFiles.Add(path);
        _keyToPath[storageKey] = path;
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = Path.GetFileName(path), ContentType = contentType,
            SizeBytes = sizeBytes, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return storageKey;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
        _db.Dispose();
    }

    private string SeedMedia(string storageKey, string contentType, string format, int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        var ext = format == "png" ? ".png" : ".jpg";
        var path = Path.Combine(Path.GetTempPath(), $"createtest_{Guid.NewGuid():N}{ext}");
        using (var fs = File.Create(path))
        {
            if (format == "png") image.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            else image.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        }
        _tempFiles.Add(path);
        _keyToPath[storageKey] = path;

        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = Path.GetFileName(path), ContentType = contentType,
            SizeBytes = new FileInfo(path).Length, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return storageKey;
    }

    /// <summary>
    /// Seeds a PNG original WITH a stored Instagram JPEG derivative, mirroring what the
    /// upload-complete flow produces. Both keys resolve to real image files.
    /// </summary>
    private string SeedPngMediaWithDerivative(string originalKey, int width, int height, int derivW = 1080, int derivH = 1080)
    {
        SeedMedia(originalKey, "image/png", "png", width, height);
        var derivKey = originalKey + ".ig.jpg";
        var derivPath = Path.Combine(Path.GetTempPath(), $"createderiv_{Guid.NewGuid():N}.jpg");
        using (var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(derivW, derivH))
        using (var fs = File.Create(derivPath))
            img.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        _tempFiles.Add(derivPath);
        _keyToPath[derivKey] = derivPath;

        var media = _db.Media.First(m => m.StorageKey == originalKey);
        media.InstagramImageStorageKey = derivKey;
        media.InstagramImageMimeType = "image/jpeg";
        media.InstagramImageSizeBytes = new FileInfo(derivPath).Length;
        media.InstagramImageWidth = derivW;
        media.InstagramImageHeight = derivH;
        media.InstagramImageGeneratedAt = DateTime.UtcNow;
        _db.SaveChanges();
        return originalKey;
    }

    private Guid SeedFacebookPage()
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta, IsConnected = true };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, MetaConnectionId = conn.Id,
            PageId = "FBPAGE", Name = "FB", AccessToken = "TOKEN", IsConnected = true,
        };
        _db.Add(conn); _db.Add(page); _db.SaveChanges();
        return page.Id;
    }

    private Guid SeedInstagramAccount()
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta, IsConnected = true };
        var ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, MetaConnectionId = conn.Id,
            PageId = "FBPAGE", PageName = "FB", IgBusinessId = "IGBIZ", Username = "u", IsConnected = true,
        };
        _db.Add(conn); _db.Add(ig); _db.SaveChanges();
        return ig.Id;
    }

    private static List<Dictionary<string, object?>>? ExtractMediaErrors(ProblemDetails pd)
        => pd.Extensions.TryGetValue("mediaErrors", out var v) ? v as List<Dictionary<string, object?>> : null;

    // ── Facebook accepts PNG ────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePost_FacebookPng_IsAccepted()
    {
        var pageId = SeedFacebookPage();
        var key = SeedMedia("fb-png", "image/png", "png", 1200, 630);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId);

        var result = await _controller.CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // ── Instagram blocks PNG ────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePost_InstagramPng_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var key = SeedMedia("ig-png", "image/png", "png", 1080, 1080);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal("MEDIA_VALIDATION_FAILED", pd.Extensions["code"]);
        var errors = ExtractMediaErrors(pd);
        Assert.NotNull(errors);
        // Phase 3: a PNG without an Instagram JPEG derivative is blocked with the
        // derivative-missing code (a derivative is normally generated at upload-complete).
        Assert.Contains(errors!, e => (string?)e["platform"] == "Instagram"
                                   && (string?)e["code"] == DTOs.MediaValidationErrorCodes.InstagramDerivativeMissing);

        // No post row created.
        Assert.Empty(await _db.Posts.ToListAsync());
    }

    [Fact]
    public async Task CreatePost_InstagramPngWithValidDerivative_IsAccepted()
    {
        var igId = SeedInstagramAccount();
        var key = SeedPngMediaWithDerivative("ig-png-ok", 2000, 2000);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_InstagramValidJpeg_IsAccepted()
    {
        var igId = SeedInstagramAccount();
        var key = SeedMedia("ig-jpg", "image/jpeg", "jpeg", 1080, 1080);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_InstagramOverMaxWidthValidAspect_IsAccepted_WarningIgnored()
    {
        var igId = SeedInstagramAccount();
        var key = SeedMedia("ig-wide", "image/jpeg", "jpeg", 1500, 1500); // > 1440 → warning only

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task CreatePost_InstagramBadAspect_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var key = SeedMedia("ig-aspect", "image/jpeg", "jpeg", 1600, 400); // aspect 4.0 → invalid

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        var errors = ExtractMediaErrors(pd);
        Assert.Contains(errors!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.AspectRatioInvalid);
    }

    // ── Instagram carousel: one bad item is identified ──────────────────────────

    [Fact]
    public async Task CreatePost_InstagramCarousel_OneInvalidItem_IsRejected_AndIdentifiesItem()
    {
        var igId = SeedInstagramAccount();
        var k0 = SeedMedia("c0", "image/jpeg", "jpeg", 1080, 1080);
        var k1 = SeedMedia("c1", "image/png", "png", 1080, 1080); // PNG → invalid for IG
        var k2 = SeedMedia("c2", "image/jpeg", "jpeg", 1080, 1080);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: null, MediaType: null, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId,
            MediaItems: new List<CreatePostMediaItem>
            {
                new(k0, MediaType.Image, 0),
                new(k1, MediaType.Image, 1),
                new(k2, MediaType.Image, 2),
            });

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        var errors = ExtractMediaErrors(pd);
        Assert.NotNull(errors);
        // The offending carousel item (order 1) is identified.
        Assert.All(errors!, e => Assert.Equal(1, Convert.ToInt32(e["order"])));
    }

    // ── Warnings don't block ────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePost_FacebookValidJpeg_NoMediaErrors()
    {
        var pageId = SeedFacebookPage();
        var key = SeedMedia("fb-jpg", "image/jpeg", "jpeg", 1200, 630);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetPageId: pageId);

        var result = await _controller.CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // ── Videos are now gated at create time (no longer passed through) ───────────

    [Fact]
    public async Task CreatePost_InstagramVideoTooLong_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var key = SeedVideoMedia("ig-vid-long", "video/mp4", 10L * 1024 * 1024);
        UseVideoMetadata(1080, 1080, durationSeconds: 75); // > 60s for IG feed

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Video, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal("MEDIA_VALIDATION_FAILED", pd.Extensions["code"]);
        var errors = ExtractMediaErrors(pd);
        Assert.Contains(errors!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.DurationTooLong);
        Assert.Empty(await _db.Posts.ToListAsync());
    }

    [Fact]
    public async Task CreatePost_InstagramValidMovVideo_IsAccepted()
    {
        var igId = SeedInstagramAccount();
        var key = SeedVideoMedia("ig-vid-mov", "video/quicktime", 20L * 1024 * 1024);
        UseVideoMetadata(1080, 1080, durationSeconds: 10, container: "mov", videoCodec: "h264", audioCodec: "aac");

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Video, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    // ── M1: media ownership enforcement (reject external / unknown / foreign keys) ──

    /// <summary>Inserts a Media row owned by a DIFFERENT workspace (no local bytes needed —
    /// the gate rejects on the workspace-scoped lookup before ever resolving bytes).</summary>
    private void SeedForeignMedia(string storageKey)
    {
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000ff"),
            StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = "foreign.jpg", ContentType = "image/jpeg",
            SizeBytes = 1024, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task CreatePost_ExternalMediaUrl_IsRejected()
    {
        var igId = SeedInstagramAccount();

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: "https://example.com/x.jpg", MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        var errors = ExtractMediaErrors(pd);
        Assert.Contains(errors!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.MediaNotFound);
        Assert.Empty(await _db.Posts.ToListAsync());
    }

    [Fact]
    public async Task CreatePost_UnknownStorageKey_IsRejected()
    {
        var igId = SeedInstagramAccount();

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: "media/never-uploaded.jpg", MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Contains(ExtractMediaErrors(pd)!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.MediaNotFound);
        Assert.Empty(await _db.Posts.ToListAsync());
    }

    [Fact]
    public async Task CreatePost_ForeignWorkspaceStorageKey_IsRejected()
    {
        var igId = SeedInstagramAccount();
        SeedForeignMedia("media/owned-elsewhere.jpg");

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: "media/owned-elsewhere.jpg", MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Contains(ExtractMediaErrors(pd)!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.MediaNotFound);
        Assert.Empty(await _db.Posts.ToListAsync());
    }

    [Fact]
    public async Task CreatePost_Carousel_WithForeignItem_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var k0 = SeedMedia("c-own-0", "image/jpeg", "jpeg", 1080, 1080);
        SeedForeignMedia("c-foreign-1");
        var k2 = SeedMedia("c-own-2", "image/jpeg", "jpeg", 1080, 1080);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: null, MediaType: null, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId,
            MediaItems: new List<CreatePostMediaItem>
            {
                new(k0, MediaType.Image, 0),
                new("c-foreign-1", MediaType.Image, 1),
                new(k2, MediaType.Image, 2),
            });

        var result = await _controller.CreatePost(req);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Contains(ExtractMediaErrors(pd)!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.MediaNotFound);
        Assert.Empty(await _db.Posts.ToListAsync());
    }

    [Fact]
    public async Task CreatePost_OwnedStorageKey_IsAccepted()
    {
        var igId = SeedInstagramAccount();
        var key = SeedMedia("ig-owned", "image/jpeg", "jpeg", 1080, 1080);

        var req = new CreatePostRequest(
            Content: "hi", MediaUrl: key, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1), PostType: PostType.Feed, TargetInstagramAccountId: igId);

        var result = await _controller.CreatePost(req);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }
}
