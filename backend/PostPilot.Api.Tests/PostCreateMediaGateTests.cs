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
    private readonly PostsController _controller;
    private readonly Dictionary<string, string> _keyToPath = new();
    private readonly List<string> _tempFiles = new();

    public PostCreateMediaGateTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

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
                Mock.Of<IVideoMetadataExtractor>(),
                NullLogger<MediaValidationService>.Instance),
            NullLogger<MediaValidationGate>.Instance);

        _controller = new PostsController(
            _db, scheduler.Object, Mock.Of<IFacebookInsightsService>(),
            workspace.Object, gate, NullLogger<PostsController>.Instance);
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
        Assert.Contains(errors!, e => (string?)e["platform"] == "Instagram"
                                   && (string?)e["code"] == DTOs.MediaValidationErrorCodes.UnsupportedMimeType);

        // No post row created.
        Assert.Empty(await _db.Posts.ToListAsync());
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
}
