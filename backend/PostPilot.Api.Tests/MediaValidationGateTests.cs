using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for the authoritative server-side <see cref="MediaValidationGate"/> using the REAL
/// <see cref="MediaValidationService"/> (real ImageSharp decode) over generated image files.
/// The storage layer is faked so each test controls exactly which bytes a key resolves to;
/// the gate's own logic (Media-row MIME lookup, per-target validation, error aggregation,
/// warnings-don't-block) is what's under test.
/// </summary>
public class MediaValidationGateTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000a2");

    private readonly AppDbContext _db;
    private readonly List<string> _tempFiles = new();

    public MediaValidationGateTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
        _db.Dispose();
    }

    // ── Image generation ───────────────────────────────────────────────────────

    private string WriteImage(string format, int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        var ext = format switch { "jpeg" => ".jpg", "png" => ".png", "webp" => ".webp", _ => throw new ArgumentException(format) };
        var path = Path.Combine(Path.GetTempPath(), $"gatetest_{Guid.NewGuid():N}{ext}");
        using (var fs = File.Create(path))
        {
            switch (format)
            {
                case "jpeg": image.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder()); break;
                case "png": image.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder()); break;
                case "webp": image.Save(fs, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder()); break;
            }
        }
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Registers a media item: writes a real image, inserts a workspace-scoped Media row with
    /// the given content type, and returns the storage key. The fake media service resolves
    /// the key back to the file path.
    /// </summary>
    private string SeedMedia(string storageKey, string contentType, string format, int width, int height, Guid? workspace = null)
    {
        var path = WriteImage(format, width, height);
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace ?? Ws,
            StorageProvider = "local-disk",
            Bucket = "",
            StorageKey = storageKey,
            OriginalFileName = Path.GetFileName(path),
            ContentType = contentType,
            SizeBytes = new FileInfo(path).Length,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return path; // keyed by storageKey in the fake below
    }

    private MediaValidationGate CreateGate(
        Dictionary<string, string> keyToPath,
        IVideoMetadataExtractor? videoExtractor = null)
    {
        var mediaService = new Mock<IMediaService>();
        // Storage keys are non-http; external URLs start with http.
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>()))
            .Returns<string>(key => Task.FromResult<string?>(keyToPath.TryGetValue(key, out var p) ? p : null));
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>())); // no-op: files cleaned in Dispose

        var validationService = new MediaValidationService(
            new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
            videoExtractor ?? Mock.Of<IVideoMetadataExtractor>(),
            NullLogger<MediaValidationService>.Instance);

        return new MediaValidationGate(
            _db, mediaService.Object, validationService, NullLogger<MediaValidationGate>.Instance);
    }

    /// <summary>
    /// A fake video metadata extractor that returns the same metadata for every path. The gate
    /// only ever validates one video per test, so a single fixed value is enough; the size check
    /// uses the Media row's SizeBytes, so callers control size via <see cref="SeedRawMedia"/>.
    /// </summary>
    private static IVideoMetadataExtractor FakeVideo(
        int width, int height, double durationSeconds,
        string container = "mp4", string videoCodec = "h264", string audioCodec = "aac",
        double? fps = 30)
    {
        var meta = new VideoMetadata(
            Width: width, Height: height, DurationSeconds: durationSeconds,
            Container: container, VideoCodec: videoCodec, AudioCodec: audioCodec,
            Fps: fps, Bitrate: null, MimeType: container == "mov" ? "video/quicktime" : "video/mp4");
        var mock = new Mock<IVideoMetadataExtractor>();
        mock.Setup(e => e.ExtractAsync(It.IsAny<string>())).ReturnsAsync(meta);
        return mock.Object;
    }

    /// <summary>
    /// Seeds a Media row whose bytes are an arbitrary placeholder file (the real metadata comes
    /// from the fake extractor). The row's ContentType + SizeBytes are authoritative, so this is
    /// how a test controls a video's declared MIME and size.
    /// </summary>
    private string SeedRawMedia(string storageKey, string contentType, long sizeBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gatetest_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[] { 0x00 });
        _tempFiles.Add(path);
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            StorageProvider = "local-disk",
            Bucket = "",
            StorageKey = storageKey,
            OriginalFileName = Path.GetFileName(path),
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return path;
    }

    /// <summary>
    /// Seeds a PNG original Media row together with its Instagram JPEG derivative (a real JPEG of
    /// the given dimensions). Returns the derivative file path so the caller maps the derivative
    /// key → bytes. This mirrors a successful upload-time PNG→JPEG conversion.
    /// </summary>
    private (string originalPath, string derivativePath) SeedMediaWithInstagramDerivative(
        string originalKey, string derivativeKey, int derivWidth, int derivHeight)
    {
        var originalPath = WriteImage("png", derivWidth, derivHeight);
        var derivativePath = WriteImage("jpeg", derivWidth, derivHeight);
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            StorageProvider = "local-disk",
            Bucket = "",
            StorageKey = originalKey,
            OriginalFileName = Path.GetFileName(originalPath),
            ContentType = "image/png",
            SizeBytes = new FileInfo(originalPath).Length,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
            InstagramImageStorageKey = derivativeKey,
            InstagramImageMimeType = "image/jpeg",
            InstagramImageSizeBytes = new FileInfo(derivativePath).Length,
            InstagramImageWidth = derivWidth,
            InstagramImageHeight = derivHeight,
            InstagramImageGeneratedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return (originalPath, derivativePath);
    }

    private static MediaGateTarget Fb => new(Platform.Facebook, Placement.Feed);
    private static MediaGateTarget Ig => new(Platform.Instagram, Placement.Feed);
    private static MediaGateTarget FbStory => new(Platform.Facebook, Placement.Story);
    private static MediaGateTarget IgStory => new(Platform.Instagram, Placement.Story);

    // ── PNG: per-target behavior ────────────────────────────────────────────────

    [Fact]
    public async Task Png_Facebook_IsValid()
    {
        var path = SeedMedia("k-fb-png", "image/png", "png", 1200, 630);
        var gate = CreateGate(new() { ["k-fb-png"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-fb-png", MediaType.Image, 0) }, new[] { Fb });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Png_Instagram_WithoutDerivative_IsBlocked()
    {
        // Phase 3: a PNG with no Instagram JPEG derivative is blocked for Instagram with the
        // derivative-missing code (a derivative is normally generated at upload time).
        var path = SeedMedia("k-ig-png", "image/png", "png", 1080, 1080);
        var gate = CreateGate(new() { ["k-ig-png"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-ig-png", MediaType.Image, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.InstagramDerivativeMissing
                                          && e.Platform == Platform.Instagram);
    }

    [Fact]
    public async Task Png_FacebookAndInstagram_BlocksWithInstagramSpecificError()
    {
        var path = SeedMedia("k-both-png", "image/png", "png", 1080, 1080);
        var gate = CreateGate(new() { ["k-both-png"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-both-png", MediaType.Image, 0) }, new[] { Fb, Ig });

        Assert.False(result.IsValid);
        // The failure is Instagram-specific; Facebook must NOT appear as a failing target.
        Assert.Contains(result.Errors, e => e.Platform == Platform.Instagram);
        Assert.DoesNotContain(result.Errors, e => e.Platform == Platform.Facebook);
    }

    // ── JPEG valid / warnings / aspect ──────────────────────────────────────────

    [Fact]
    public async Task Jpeg_Instagram_Valid_IsAccepted()
    {
        var path = SeedMedia("k-ig-jpg", "image/jpeg", "jpeg", 1080, 1080);
        var gate = CreateGate(new() { ["k-ig-jpg"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-ig-jpg", MediaType.Image, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Jpeg_Instagram_OverMaxWidth_ValidAspect_IsAcceptedWarningIgnored()
    {
        // 1500x1500 exceeds IG max width (1440) but aspect is 1:1 (valid). Meta downscales →
        // this is a WARNING, which must not block.
        var path = SeedMedia("k-ig-wide", "image/jpeg", "jpeg", 1500, 1500);
        var gate = CreateGate(new() { ["k-ig-wide"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-ig-wide", MediaType.Image, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Jpeg_Instagram_BadAspectRatio_IsBlocked()
    {
        // 1600x400 → aspect 4.0, outside IG's 0.8–1.91. Hard error.
        var path = SeedMedia("k-ig-aspect", "image/jpeg", "jpeg", 1600, 400);
        var gate = CreateGate(new() { ["k-ig-aspect"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-ig-aspect", MediaType.Image, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.AspectRatioInvalid);
    }

    // ── Carousel: identify the failing item ─────────────────────────────────────

    [Fact]
    public async Task MultiImage_Instagram_OneInvalid_BlocksAndIdentifiesItem()
    {
        var ok1 = SeedMedia("k-c0", "image/jpeg", "jpeg", 1080, 1080);
        var badPng = SeedMedia("k-c1", "image/png", "png", 1080, 1080); // PNG → invalid for IG
        var ok2 = SeedMedia("k-c2", "image/jpeg", "jpeg", 1080, 1080);
        var gate = CreateGate(new() { ["k-c0"] = ok1, ["k-c1"] = badPng, ["k-c2"] = ok2 });

        var items = new[]
        {
            new MediaGateItem("k-c0", MediaType.Image, 0),
            new MediaGateItem("k-c1", MediaType.Image, 1),
            new MediaGateItem("k-c2", MediaType.Image, 2),
        };
        var result = await gate.ValidateAsync(Ws, items, new[] { Ig });

        Assert.False(result.IsValid);
        // Only the offending item (order 1) is reported.
        Assert.All(result.Errors, e => Assert.Equal(1, e.Order));
        // PNG without a derivative is blocked for Instagram with the derivative-missing code.
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.InstagramDerivativeMissing);
    }

    // ── PNG with Instagram JPEG derivative (derivative-aware) ───────────────────

    [Fact]
    public async Task Png_Instagram_WithDerivative_IsNotInvalid()
    {
        // A PNG that has a valid Instagram JPEG derivative validates against the DERIVATIVE,
        // so it must NOT be rejected for being non-JPEG. 1080x1080 → valid IG feed image.
        var (_, derivPath) = SeedMediaWithInstagramDerivative("k-png-ok", "k-png-ok-deriv", 1080, 1080);
        var gate = CreateGate(new() { ["k-png-ok-deriv"] = derivPath });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-png-ok", MediaType.Image, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Png_Instagram_WithDerivative_OutOfRatio_IsBlocked()
    {
        // Conversion does NOT auto-fix aspect ratio: a derivative whose ratio is out of range
        // (1600x400 → 4.0, outside IG's 0.8–1.91) is still blocked after conversion.
        var (_, derivPath) = SeedMediaWithInstagramDerivative("k-png-bad", "k-png-bad-deriv", 1600, 400);
        var gate = CreateGate(new() { ["k-png-bad-deriv"] = derivPath });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-png-bad", MediaType.Image, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.AspectRatioInvalid);
    }

    [Fact]
    public async Task Png_Facebook_WithDerivativePresent_StillValidatesOriginal()
    {
        // Facebook never uses the Instagram derivative — the original PNG is valid for FB.
        var (origPath, _) = SeedMediaWithInstagramDerivative("k-png-fb", "k-png-fb-deriv", 1200, 630);
        var gate = CreateGate(new() { ["k-png-fb"] = origPath });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-png-fb", MediaType.Image, 0) }, new[] { Fb });

        Assert.True(result.IsValid);
    }

    // ── Video validation (gate now validates videos, not just images) ───────────

    [Fact]
    public async Task Video_InstagramFeed_OverMaxSize_IsBlocked()
    {
        var path = SeedRawMedia("v-ig-big", "video/mp4", 101L * 1024 * 1024); // 101MB > 100MB
        var gate = CreateGate(new() { ["v-ig-big"] = path }, FakeVideo(1080, 1080, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-big", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge);
    }

    [Fact]
    public async Task Video_InstagramFeed_TooLong_IsBlocked()
    {
        var path = SeedRawMedia("v-ig-long", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-long"] = path }, FakeVideo(1080, 1080, 75)); // 75s > 60s

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-long", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    [Fact]
    public async Task Video_InstagramStory_BadAspect_IsBlocked()
    {
        var path = SeedRawMedia("v-ig-story", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-story"] = path }, FakeVideo(1080, 1080, 10)); // 1:1 outside 0.5–0.75

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-story", MediaType.Video, 0) }, new[] { IgStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.AspectRatioInvalid);
    }

    [Fact]
    public async Task Video_FacebookStory_TooLong_IsBlocked()
    {
        var path = SeedRawMedia("v-fb-story-long", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-story-long"] = path }, FakeVideo(1080, 1920, 150)); // 150s > 120s

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-long", MediaType.Video, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    [Fact]
    public async Task Video_FacebookStory_BadAspect_IsBlocked()
    {
        var path = SeedRawMedia("v-fb-story-aspect", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-story-aspect"] = path }, FakeVideo(1080, 1080, 10)); // 1:1

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-aspect", MediaType.Video, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.AspectRatioInvalid);
    }

    [Fact]
    public async Task Video_FacebookFeed_Over200MB_IsBlocked_NotOneGB()
    {
        // FB feed video cap is the real 200MB upload ceiling, NOT Meta's 1GB API limit.
        var path = SeedRawMedia("v-fb-201", "video/mp4", 201L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-201"] = path }, FakeVideo(1280, 720, 30));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-201", MediaType.Video, 0) }, new[] { Fb });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge);
    }

    [Fact]
    public async Task Video_Mp4_ValidMetadata_Passes()
    {
        var path = SeedRawMedia("v-fb-ok", "video/mp4", 50L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-ok"] = path }, FakeVideo(1280, 720, 30));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-ok", MediaType.Video, 0) }, new[] { Fb });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_Mov_ValidIphoneMetadata_Passes()
    {
        // MOV (video/quicktime) with H.264/AAC is the typical iPhone capture → must pass.
        var path = SeedRawMedia("v-ig-mov", "video/quicktime", 20L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-mov"] = path },
            FakeVideo(1080, 1080, 10, container: "mov", videoCodec: "h264", audioCodec: "aac"));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-mov", MediaType.Video, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_FacebookFeed_Hevc_Passes()
    {
        // H.265/HEVC is supported (e.g. modern iPhone MOV captures).
        var path = SeedRawMedia("v-fb-hevc", "video/quicktime", 30L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-hevc"] = path },
            FakeVideo(1280, 720, 30, container: "mov", videoCodec: "hevc", audioCodec: "aac"));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-hevc", MediaType.Video, 0) }, new[] { Fb });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_Mov_UnsupportedCodec_IsBlocked()
    {
        // MOV is accepted for iPhone compatibility, but an unsupported internal codec (e.g.
        // ProRes) still fails when metadata is available.
        var path = SeedRawMedia("v-ig-prores", "video/quicktime", 20L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-prores"] = path },
            FakeVideo(1080, 1080, 10, container: "mov", videoCodec: "prores", audioCodec: "aac"));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-prores", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedVideoCodec);
    }

    [Fact]
    public async Task Video_Mov_UnsupportedAudio_IsBlocked()
    {
        var path = SeedRawMedia("v-ig-pcm", "video/quicktime", 20L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-pcm"] = path },
            FakeVideo(1080, 1080, 10, container: "mov", videoCodec: "h264", audioCodec: "pcm_s16le"));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-pcm", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedAudioCodec);
    }

    // ── ValidateForDisplayAsync (advisory /api/media/validate path) ──────────────

    [Fact]
    public async Task ValidateForDisplay_InstagramPng_WithDerivative_IsNotInvalid()
    {
        // The advisory endpoint must mirror the gate: a valid PNG (with derivative) is NOT
        // marked Invalid, so the composer never blocks a publishable PNG for Instagram.
        var (_, derivPath) = SeedMediaWithInstagramDerivative("d-png-ok", "d-png-ok-deriv", 1080, 1080);
        var gate = CreateGate(new() { ["d-png-ok-deriv"] = derivPath });

        var result = await gate.ValidateForDisplayAsync(Ws,
            new MediaGateItem("d-png-ok", MediaType.Image, 0), Ig);

        Assert.NotEqual(ValidationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task ValidateForDisplay_InstagramPng_WithoutDerivative_IsInvalid()
    {
        var path = SeedMedia("d-png-missing", "image/png", "png", 1080, 1080);
        var gate = CreateGate(new() { ["d-png-missing"] = path });

        var result = await gate.ValidateForDisplayAsync(Ws,
            new MediaGateItem("d-png-missing", MediaType.Image, 0), Ig);

        Assert.Equal(ValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.InstagramDerivativeMissing);
    }

    [Fact]
    public async Task ValidateForDisplay_InvalidVideo_IsInvalid()
    {
        var path = SeedRawMedia("d-vid-long", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["d-vid-long"] = path }, FakeVideo(1080, 1080, 75));

        var result = await gate.ValidateForDisplayAsync(Ws,
            new MediaGateItem("d-vid-long", MediaType.Video, 0), Ig);

        Assert.Equal(ValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    // ── Pass-through cases ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExternalUrl_IsPassedThrough_NotValidated()
    {
        var gate = CreateGate(new());
        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("https://example.com/legacy.png", MediaType.Image, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task KeyOwnedByOtherWorkspace_IsSkipped_NotBlocked()
    {
        // Media row exists but in a DIFFERENT workspace → no row for Ws → skipped (not blocked).
        var otherWs = Guid.NewGuid();
        var path = SeedMedia("k-other", "image/png", "png", 1080, 1080, workspace: otherWs);
        var gate = CreateGate(new() { ["k-other"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-other", MediaType.Image, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }
}
