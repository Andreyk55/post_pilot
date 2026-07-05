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
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Integration tests for the authoritative media gate inside <see cref="PostsController.UpdatePost"/>.
/// Wires the REAL <see cref="MediaValidationGate"/> so an EDIT that swaps in media the SPA would
/// block (but a crafted client could replay) is rejected server-side, just like CreatePost. Closes
/// the bypass where PUT /api/posts/{id} could otherwise save media that only fails later at
/// publisher time. Storage is faked; DB and controller validation are real.
/// </summary>
public class PostUpdateMediaGateTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000d5");

    private readonly AppDbContext _db;
    private PostsController _controller;
    private readonly Dictionary<string, string> _keyToPath = new();
    private readonly List<string> _tempFiles = new();

    public PostUpdateMediaGateTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        _controller = BuildController(Mock.Of<IVideoMetadataExtractor>());
    }

    private PostsController BuildController(IVideoMetadataExtractor videoExtractor)
    {
        var scheduler = new Mock<IPostScheduler>();
        scheduler.Setup(s => s.RescheduleAsync(It.IsAny<Post>())).ReturnsAsync(new ScheduleResult(true, "arn", null));

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

    private void UseVideoMetadata(int width, int height, double durationSeconds,
        string container = "mp4", string videoCodec = "h264", string audioCodec = "aac", double? fps = 30)
    {
        var meta = new VideoMetadata(width, height, durationSeconds, container, videoCodec, audioCodec,
            fps, null, container == "mov" ? "video/quicktime" : "video/mp4");
        var ext = new Mock<IVideoMetadataExtractor>();
        ext.Setup(e => e.ExtractAsync(It.IsAny<string>())).ReturnsAsync(meta);
        _controller = BuildController(ext.Object);
    }

    private Media SeedVideoMedia(string storageKey, string contentType, long sizeBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"updatetest_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[] { 0x00 });
        _tempFiles.Add(path);
        _keyToPath[storageKey] = path;
        var media = new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = Path.GetFileName(path), ContentType = contentType,
            SizeBytes = sizeBytes, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        };
        _db.Media.Add(media);
        _db.SaveChanges();
        return media;
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
        _db.Dispose();
    }

    private Media SeedMedia(string storageKey, string contentType, string format, int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        var ext = format == "png" ? ".png" : ".jpg";
        var path = Path.Combine(Path.GetTempPath(), $"updatetest_{Guid.NewGuid():N}{ext}");
        using (var fs = File.Create(path))
        {
            if (format == "png") image.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            else image.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        }
        _tempFiles.Add(path);
        _keyToPath[storageKey] = path;

        var media = new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = Path.GetFileName(path), ContentType = contentType,
            SizeBytes = new FileInfo(path).Length, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        };
        _db.Media.Add(media);
        _db.SaveChanges();
        return media;
    }

    /// <summary>
    /// Seeds a PNG original WITH a stored Instagram JPEG derivative (both keys map to real
    /// image files), mirroring what the upload-complete flow produces. The derivative is a
    /// valid 1080x1080 JPEG unless overridden.
    /// </summary>
    private Media SeedPngMediaWithDerivative(string originalKey, int width, int height, int derivW = 1080, int derivH = 1080)
    {
        var originalMedia = SeedMedia(originalKey, "image/png", "png", width, height);
        var derivKey = originalKey + ".ig.jpg";
        var derivPath = Path.Combine(Path.GetTempPath(), $"updatederiv_{Guid.NewGuid():N}.jpg");
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
        return originalMedia;
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

    /// <summary>
    /// Seeds a Scheduled post (the only status UpdatePost allows) with known-good starting media,
    /// so each test can attempt to edit it to new media and assert accept/reject independently.
    /// </summary>
    private Post SeedScheduledPost(Platform platform, string startingMediaKey, Guid? targetPageId, Guid? targetIgId)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            Content = "original",
            MediaUrl = startingMediaKey,
            MediaType = MediaType.Image,
            PostType = PostType.Feed,
            Platform = platform,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            TargetPageId = targetPageId,
            TargetInstagramAccountId = targetIgId,
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post);
        _db.SaveChanges();
        return post;
    }

    private static List<Dictionary<string, object?>>? ExtractMediaErrors(ProblemDetails pd)
        => pd.Extensions.TryGetValue("mediaErrors", out var v) ? v as List<Dictionary<string, object?>> : null;

    // ── Instagram blocks PNG on edit ────────────────────────────────────────────

    [Fact]
    public async Task UpdatePost_InstagramPngWithoutDerivative_IsRejected()
    {
        // Phase 3: a PNG with no Instagram JPEG derivative is rejected on edit with the
        // derivative-missing code.
        var igId = SeedInstagramAccount();
        var goodMedia = SeedMedia("u-ig-good", "image/jpeg", "jpeg", 1080, 1080);
        var pngMedia = SeedMedia("u-ig-png", "image/png", "png", 1080, 1080);
        var post = SeedScheduledPost(Platform.Instagram, goodMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: pngMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal("MEDIA_VALIDATION_FAILED", pd.Extensions["code"]);
        var errors = ExtractMediaErrors(pd);
        Assert.NotNull(errors);
        Assert.Contains(errors!, e => (string?)e["platform"] == "Instagram"
                                   && (string?)e["code"] == DTOs.MediaValidationErrorCodes.InstagramDerivativeMissing);
    }

    [Fact]
    public async Task UpdatePost_InstagramPngWithValidDerivative_IsAccepted()
    {
        // Phase 3: a PNG WITH a valid Instagram JPEG derivative is accepted on edit.
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-ig-start2", "image/jpeg", "jpeg", 1080, 1080);
        var pngMedia = SeedPngMediaWithDerivative("u-ig-png-ok", 2000, 2000);
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: pngMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        Assert.IsType<NoContentResult>(result);
    }

    // ── Facebook accepts PNG on edit ────────────────────────────────────────────

    [Fact]
    public async Task UpdatePost_FacebookPng_IsAccepted()
    {
        var pageId = SeedFacebookPage();
        var goodMedia = SeedMedia("u-fb-good", "image/jpeg", "jpeg", 1200, 630);
        var pngMedia = SeedMedia("u-fb-png", "image/png", "png", 1200, 630);
        var post = SeedScheduledPost(Platform.Facebook, goodMedia.StorageKey, targetPageId: pageId, targetIgId: null);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Facebook,
            ScheduledAt: post.ScheduledAt, TargetPageId: pageId, MediaId: pngMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        Assert.IsType<NoContentResult>(result);
    }

    // ── Videos are gated on edit too ────────────────────────────────────────────

    [Fact]
    public async Task UpdatePost_InstagramVideoTooLong_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-ig-vstart", "image/jpeg", "jpeg", 1080, 1080);
        var videoMedia = SeedVideoMedia("u-ig-vbad", "video/mp4", 10L * 1024 * 1024);
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);
        UseVideoMetadata(1080, 1080, durationSeconds: 181); // > 180s MVP cap for IG feed

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Video, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: videoMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal("MEDIA_VALIDATION_FAILED", pd.Extensions["code"]);
        var errors = ExtractMediaErrors(pd);
        Assert.Contains(errors!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    // ── Instagram accepts valid JPEG on edit ────────────────────────────────────

    [Fact]
    public async Task UpdatePost_InstagramValidJpeg_IsAccepted()
    {
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-ig-start", "image/jpeg", "jpeg", 1080, 1080);
        var newMedia = SeedMedia("u-ig-new", "image/jpeg", "jpeg", 1080, 1350);
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: newMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        Assert.IsType<NoContentResult>(result);

        var saved = await _db.Posts.AsNoTracking().FirstAsync(p => p.Id == post.Id);
        Assert.Equal(newMedia.StorageKey, saved.MediaUrl);
        Assert.Equal("edited", saved.Content);
    }

    // ── Over-max-width but valid aspect → warning only → accepted ────────────────

    [Fact]
    public async Task UpdatePost_InstagramOverMaxWidthValidAspect_IsAccepted_WarningIgnored()
    {
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-ig-start2", "image/jpeg", "jpeg", 1080, 1080);
        var wideMedia = SeedMedia("u-ig-wide", "image/jpeg", "jpeg", 1500, 1500); // > 1440 → warning only
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: wideMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        Assert.IsType<NoContentResult>(result);
    }

    // ── Invalid aspect ratio → rejected ─────────────────────────────────────────

    [Fact]
    public async Task UpdatePost_InstagramBadAspect_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-ig-start3", "image/jpeg", "jpeg", 1080, 1080);
        var badMedia = SeedMedia("u-ig-aspect", "image/jpeg", "jpeg", 1600, 400); // aspect 4.0 → invalid
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: badMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);
        var errors = ExtractMediaErrors(pd);
        Assert.NotNull(errors);
        Assert.Contains(errors!, e => (string?)e["code"] == DTOs.MediaValidationErrorCodes.AspectRatioInvalid);
    }

    // ── Failed validation must not persist the update ───────────────────────────

    [Fact]
    public async Task UpdatePost_FailedValidation_DoesNotPersist()
    {
        var igId = SeedInstagramAccount();
        var goodMedia = SeedMedia("u-ig-persist-good", "image/jpeg", "jpeg", 1080, 1080);
        var pngMedia = SeedMedia("u-ig-persist-png", "image/png", "png", 1080, 1080);
        var post = SeedScheduledPost(Platform.Instagram, goodMedia.StorageKey, targetPageId: null, targetIgId: igId);
        var originalScheduledAt = post.ScheduledAt;

        var req = new UpdatePostRequest(
            Content: "edited-should-not-save", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: originalScheduledAt.AddHours(1), TargetInstagramAccountId: igId, MediaId: pngMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        Assert.IsType<BadRequestObjectResult>(result);

        var saved = await _db.Posts.AsNoTracking().FirstAsync(p => p.Id == post.Id);
        Assert.Equal("original", saved.Content);
        Assert.Equal(goodMedia.StorageKey, saved.MediaUrl);
        Assert.Equal(originalScheduledAt, saved.ScheduledAt);
    }

    // ── Error response shape matches CreatePost media validation failure ─────────

    [Fact]
    public async Task UpdatePost_MediaValidationFailure_MatchesCreatePostShape()
    {
        var igId = SeedInstagramAccount();
        var goodMedia = SeedMedia("u-ig-shape-good", "image/jpeg", "jpeg", 1080, 1080);
        var pngMedia = SeedMedia("u-ig-shape-png", "image/png", "png", 1080, 1080);
        var post = SeedScheduledPost(Platform.Instagram, goodMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: pngMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var pd = Assert.IsType<ProblemDetails>(bad.Value);

        // Same top-level extensions as CreatePost: code + platforms + mediaErrors.
        Assert.Equal("MEDIA_VALIDATION_FAILED", pd.Extensions["code"]);
        Assert.True(pd.Extensions.ContainsKey("platforms"));
        var platforms = Assert.IsType<string[]>(pd.Extensions["platforms"]);
        Assert.Contains("Instagram", platforms);

        // Same per-item error entry shape: order, platform, placement, code, field, message.
        var errors = ExtractMediaErrors(pd);
        Assert.NotNull(errors);
        var entry = Assert.Single(errors!);
        Assert.True(entry.ContainsKey("order"));
        Assert.True(entry.ContainsKey("platform"));
        Assert.True(entry.ContainsKey("placement"));
        Assert.True(entry.ContainsKey("code"));
        Assert.True(entry.ContainsKey("field"));
        Assert.True(entry.ContainsKey("message"));
        Assert.Equal("Instagram", (string?)entry["platform"]);
        Assert.Equal("Feed", (string?)entry["placement"]);
    }

    // ── M1: editing to external / unknown / foreign media is rejected ────────────

    /// <summary>Inserts a Media row owned by a DIFFERENT workspace so the gate rejects it.</summary>
    private Media SeedForeignMedia(string storageKey)
    {
        var media = new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000ff"),
            StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = "foreign.jpg", ContentType = "image/jpeg",
            SizeBytes = 1024, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        };
        _db.Media.Add(media);
        _db.SaveChanges();
        return media;
    }

    [Theory]
    [InlineData("https://example.com/x.jpg")]      // external URL
    [InlineData("media/never-uploaded.jpg")]        // unknown key
    public async Task UpdatePost_ToExternalOrRawStorageKey_IsRejected(string newMedia)
    {
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-own-start", "image/jpeg", "jpeg", 1080, 1080);
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: newMedia, MediaType: MediaType.Image, Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId);

        var result = await _controller.UpdatePost(post.Id, req);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, obj.StatusCode);
        var pd = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal("UNSUPPORTED_MEDIA_REFERENCE", pd.Extensions["code"]);

        // The edit must NOT persist: the post still points at its original media.
        var fresh = await _db.Posts.FirstAsync(p => p.Id == post.Id);
        Assert.Equal(startMedia.StorageKey, fresh.MediaUrl);
    }

    [Fact]
    public async Task UpdatePost_ToUnknownMediaId_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-own-start2", "image/jpeg", "jpeg", 1080, 1080);
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: Guid.NewGuid());

        var result = await _controller.UpdatePost(post.Id, req);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, obj.StatusCode);
        var pd = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(DTOs.MediaValidationErrorCodes.MediaNotFound, pd.Extensions["code"]);

        var fresh = await _db.Posts.FirstAsync(p => p.Id == post.Id);
        Assert.Equal(startMedia.StorageKey, fresh.MediaUrl);
    }

    [Fact]
    public async Task UpdatePost_ToForeignWorkspaceMedia_IsRejected()
    {
        var igId = SeedInstagramAccount();
        var startMedia = SeedMedia("u-own-start3", "image/jpeg", "jpeg", 1080, 1080);
        var foreignMedia = SeedForeignMedia("media/owned-elsewhere.jpg");
        var post = SeedScheduledPost(Platform.Instagram, startMedia.StorageKey, targetPageId: null, targetIgId: igId);

        var req = new UpdatePostRequest(
            Content: "edited", MediaUrl: null, MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: post.ScheduledAt, TargetInstagramAccountId: igId, MediaId: foreignMedia.Id);

        var result = await _controller.UpdatePost(post.Id, req);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, obj.StatusCode);
        var pd = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.Equal(DTOs.MediaValidationErrorCodes.MediaNotFound, pd.Extensions["code"]);

        var fresh = await _db.Posts.FirstAsync(p => p.Id == post.Id);
        Assert.Equal(startMedia.StorageKey, fresh.MediaUrl);
    }
}
