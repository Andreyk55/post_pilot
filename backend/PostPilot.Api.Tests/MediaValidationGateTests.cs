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
        string container = "mp4", string? videoCodec = "h264", string? audioCodec = "aac",
        double? fps = 30, bool hasVideoStream = true)
    {
        var meta = new VideoMetadata(
            Width: width, Height: height, DurationSeconds: durationSeconds,
            Container: container, VideoCodec: videoCodec, AudioCodec: audioCodec,
            Fps: fps, Bitrate: null, MimeType: container == "mov" ? "video/quicktime" : "video/mp4",
            HasVideoStream: hasVideoStream);
        var mock = new Mock<IVideoMetadataExtractor>();
        mock.Setup(e => e.ExtractAsync(It.IsAny<string>())).ReturnsAsync(meta);
        return mock.Object;
    }

    /// <summary>
    /// A video extractor that returns metadata per local file path, so a single carousel test can
    /// mix a valid and an invalid video item. A path absent from the map returns null (unreadable).
    /// </summary>
    private static IVideoMetadataExtractor FakeVideoByPath(Dictionary<string, VideoMetadata?> pathToMeta)
    {
        var mock = new Mock<IVideoMetadataExtractor>();
        mock.Setup(e => e.ExtractAsync(It.IsAny<string>()))
            .Returns((string path) => Task.FromResult(pathToMeta.TryGetValue(path, out var m) ? m : null));
        return mock.Object;
    }

    private static VideoMetadata Vid(double durationSeconds, int width = 1080, int height = 1080) =>
        new(width, height, durationSeconds, "mp4", "h264", "aac", 30, null, "video/mp4");

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
    public async Task Jpeg_Instagram_LargeSquare_ValidAspect_IsAccepted()
    {
        // 1500x1500 has no dimension rule to trip (IG Feed has no max width now) and a valid 1:1
        // aspect, so it is simply accepted. Meta downscales oversized images itself.
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
    public async Task Video_InstagramFeed_Over50MB_IsBlocked_WithMbMessage()
    {
        var path = SeedRawMedia("v-ig-big", "video/mp4", 52_428_801L); // 50MB + 1 byte
        var gate = CreateGate(new() { ["v-ig-big"] = path }, FakeVideo(1080, 1080, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-big", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge
            && e.Message == "This video is too large. Instagram videos can be up to 50MB."
            && !e.Message.Contains("52428800") && !e.Message.Contains("52,428,800")
            && !e.Message.Contains("100MB"));
    }

    [Fact]
    public async Task Video_InstagramFeed_AtExactly50MB_IsAccepted()
    {
        var path = SeedRawMedia("v-ig-at-50", "video/mp4", 52_428_800L);
        var gate = CreateGate(new() { ["v-ig-at-50"] = path }, FakeVideo(1080, 1080, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-at-50", MediaType.Video, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramFeed_TooLong_IsBlocked()
    {
        var path = SeedRawMedia("v-ig-long", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-long"] = path }, FakeVideo(1080, 1080, 181)); // 181s > 180s MVP cap

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-long", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong
            && e.Message == "Feed videos must be between 3 and 180 seconds.");
    }

    [Fact]
    public async Task Video_InstagramFeed_180Seconds_Passes()
    {
        var path = SeedRawMedia("v-ig-180", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-180"] = path }, FakeVideo(1080, 1080, 180));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-180", MediaType.Video, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramFeed_Vertical9x16_Passes()
    {
        // A single IG feed video publishes as a Reel; the standard vertical 9:16 format
        // (previously blocked by the 0.8 aspect floor) must pass.
        var path = SeedRawMedia("v-ig-reel", "video/mp4", 20L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-reel"] = path }, FakeVideo(1080, 1920, 30));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-reel", MediaType.Video, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramFeed_3Seconds_Passes()
    {
        // Inclusive lower boundary for a single Feed video.
        var path = SeedRawMedia("v-ig-3s", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-3s"] = path }, FakeVideo(1080, 1080, 3));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-3s", MediaType.Video, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramFeed_JustBelow3Seconds_IsBlocked()
    {
        var path = SeedRawMedia("v-ig-2_99", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-2_99"] = path }, FakeVideo(1080, 1080, 2.99));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-2_99", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.DurationTooShort
            && e.Message == "Feed videos must be between 3 and 180 seconds.");
    }

    [Fact]
    public async Task Video_InstagramFeed_CorruptOrNonVideoRenamedMp4_IsBlocked()
    {
        // A non-video file renamed to .mp4: the row says video/mp4, but ffprobe extracts no
        // readable stream (null metadata) → blocked. The declared MIME/extension is never trusted.
        var path = SeedRawMedia("v-ig-corrupt", "video/mp4", 5L * 1024 * 1024);
        var nullExtractor = new Mock<IVideoMetadataExtractor>();
        nullExtractor.Setup(e => e.ExtractAsync(It.IsAny<string>())).ReturnsAsync((VideoMetadata?)null);
        var gate = CreateGate(new() { ["v-ig-corrupt"] = path }, nullExtractor.Object);

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-corrupt", MediaType.Video, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.MetadataExtractionFailed);
    }

    [Fact]
    public async Task Video_InstagramStory_UnusualCodecFpsDimensionsAndAspect_IsAccepted()
    {
        var path = SeedRawMedia("v-ig-story", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-story"] = path },
            FakeVideo(3840, 2160, 10, videoCodec: "prores", audioCodec: "opus", fps: 120));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-story", MediaType.Video, 0) }, new[] { IgStory });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramStory_WithoutAudio_IsAccepted()
    {
        var path = SeedRawMedia("v-ig-story-no-audio", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-story-no-audio"] = path },
            FakeVideo(1080, 1920, 10, audioCodec: null));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-story-no-audio", MediaType.Video, 0) }, new[] { IgStory });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramStory_AudioOnlyMp4_IsBlocked()
    {
        var path = SeedRawMedia("v-ig-story-audio-only", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-story-audio-only"] = path },
            FakeVideo(0, 0, 10, videoCodec: null, audioCodec: "aac", fps: null, hasVideoStream: false));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-story-audio-only", MediaType.Video, 0) }, new[] { IgStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.VideoStreamMissing);
    }

    [Fact]
    public async Task Video_InstagramStory_Over50MB_IsBlocked_WithMbMessage()
    {
        var path = SeedRawMedia("v-ig-story-big", "video/mp4", 52_428_801L);
        var gate = CreateGate(new() { ["v-ig-story-big"] = path }, FakeVideo(720, 1280, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-story-big", MediaType.Video, 0) }, new[] { IgStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge
            && e.Message == "This video is too large. Instagram videos can be up to 50MB."
            && !e.Message.Contains("52428800") && !e.Message.Contains("52,428,800")
            && !e.Message.Contains("100MB"));
    }

    [Fact]
    public async Task Video_InstagramStory_AtExactly50MB_IsAccepted()
    {
        var path = SeedRawMedia("v-ig-story-at-50", "video/mp4", 52_428_800L);
        var gate = CreateGate(new() { ["v-ig-story-at-50"] = path }, FakeVideo(720, 1280, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-story-at-50", MediaType.Video, 0) }, new[] { IgStory });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_FacebookStory_TooLong_IsBlocked()
    {
        var path = SeedRawMedia("v-fb-story-long", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-story-long"] = path }, FakeVideo(1080, 1920, 91)); // 91s > 90s FB Story cap

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-long", MediaType.Video, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong
            && e.Message == "Story videos must be between 3 and 90 seconds.");
    }

    // ── Facebook Story video duration boundaries: 3–90s (inclusive) ─────────────

    [Theory]
    [InlineData(3)]   // inclusive lower boundary
    [InlineData(60)]  // mid-range
    [InlineData(90)]  // inclusive upper boundary
    public async Task Video_FacebookStory_WithinDurationRange_Passes(double durationSeconds)
    {
        var key = $"v-fb-story-dur-ok-{durationSeconds}";
        var path = SeedRawMedia(key, "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path }, FakeVideo(1080, 1920, durationSeconds));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Video, 0) }, new[] { FbStory });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_FacebookStory_JustBelow3Seconds_IsBlocked()
    {
        // 2.99s is below the 3s floor (backend compares duration < 3), so it must be rejected.
        var path = SeedRawMedia("v-fb-story-2_99", "video/mp4", 5L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-story-2_99"] = path }, FakeVideo(1080, 1920, 2.99));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-2_99", MediaType.Video, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.DurationTooShort
            && e.Message == "Story videos must be between 3 and 90 seconds.");
    }

    // ── Story video duration boundaries: Instagram Story keeps its 3–60s window ──

    [Theory]
    [InlineData(Platform.Facebook)]
    [InlineData(Platform.Instagram)]
    public async Task Video_Story_60Seconds_Passes(Platform platform)
    {
        // 60s is within Instagram Story (3–60) and Facebook Story (3–90), so it passes for both.
        var key = $"v-story-60-{platform}";
        var path = SeedRawMedia(key, "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path }, FakeVideo(1080, 1920, 60));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Video, 0) },
            new[] { new MediaGateTarget(platform, Placement.Story) });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramStory_61Seconds_IsBlocked()
    {
        // Instagram Story stays capped at 60s (Meta limit) — 61s is rejected. (Facebook Story
        // now allows up to 90s; see Video_FacebookStory_WithinDurationRange_Passes.)
        var path = SeedRawMedia("v-ig-story-61", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-ig-story-61"] = path }, FakeVideo(1080, 1920, 61));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-ig-story-61", MediaType.Video, 0) }, new[] { IgStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    [Theory]
    [InlineData(Platform.Facebook)]
    [InlineData(Platform.Instagram)]
    public async Task Video_Story_Under3Seconds_IsBlocked(Platform platform)
    {
        var key = $"v-story-2s-{platform}";
        var path = SeedRawMedia(key, "video/mp4", 5L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path }, FakeVideo(1080, 1920, 2));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Video, 0) },
            new[] { new MediaGateTarget(platform, Placement.Story) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooShort);
    }

    // ── FB Story: NO dimension / aspect-ratio / FPS validation ──────────────────
    // Facebook Story media is validated ONLY for type, size, decodability, duration, and codecs.
    // Any shape or resolution is accepted. These pin that every former dimension / aspect / fps
    // REJECTION now passes, while type/size/duration/codec rejections remain enforced.

    [Theory]
    [InlineData(1920, 1080)] // landscape (was blocked: aspect 1.78 outside 0.50–0.75)
    [InlineData(1080, 1080)] // square (was blocked: aspect 1.0)
    [InlineData(480, 854)]   // below the old 540x960 minimum (was DimensionsTooSmall)
    [InlineData(2160, 3840)] // above the old 1080x1920 maximum (was DimensionsTooLarge)
    [InlineData(3000, 300)]  // extremely wide
    public async Task Video_FacebookStory_AnyDimensionsOrAspect_IsAccepted(int width, int height)
    {
        var key = $"v-fb-story-dims-{width}x{height}";
        var path = SeedRawMedia(key, "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path }, FakeVideo(width, height, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Video, 0) }, new[] { FbStory });

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(15)] // below the old 23 fps floor (was FpsTooLow)
    [InlineData(90)] // above the old 60 fps ceiling (was FpsTooHigh)
    public async Task Video_FacebookStory_AnyFrameRate_IsAccepted(double fps)
    {
        var key = $"v-fb-story-fps-{fps}";
        var path = SeedRawMedia(key, "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path }, FakeVideo(1080, 1920, 10, fps: fps));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Video, 0) }, new[] { FbStory });

        Assert.True(result.IsValid);
    }

    // ── FB Story: NO video/audio codec validation (Meta decides at publish) ─────
    // A readable MP4/MOV within the size + duration limits is accepted regardless of its video or
    // audio codec — including no audio stream and unknown/missing codec names. Codecs that were
    // previously rejected (ProRes, VP9, AV1, PCM, MP3, Opus) now pass. Cross-placement enforcement
    // is proved separately below.
    [Theory]
    [InlineData("h264", "aac")]
    [InlineData("hevc", "aac")]
    [InlineData("prores", "aac")]     // previously UNSUPPORTED_VIDEO_CODEC
    [InlineData("vp9", "aac")]        // previously UNSUPPORTED_VIDEO_CODEC
    [InlineData("av1", "aac")]        // previously UNSUPPORTED_VIDEO_CODEC
    [InlineData("h264", "pcm_s16le")] // previously UNSUPPORTED_AUDIO_CODEC
    [InlineData("h264", "mp3")]       // previously UNSUPPORTED_AUDIO_CODEC
    [InlineData("h264", "opus")]      // previously UNSUPPORTED_AUDIO_CODEC
    [InlineData("h264", null)]        // no audio stream
    [InlineData(null, null)]          // unknown/missing codec names (extraction otherwise succeeded)
    public async Task Video_FacebookStory_AnyVideoOrAudioCodec_IsAccepted(string? videoCodec, string? audioCodec)
    {
        var key = $"v-fb-story-codec-{videoCodec ?? "none"}-{audioCodec ?? "none"}";
        var path = SeedRawMedia(key, "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path },
            FakeVideo(1080, 1920, 10, videoCodec: videoCodec, audioCodec: audioCodec));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Video, 0) }, new[] { FbStory });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_FacebookStory_UnsupportedContainer_IsStillBlocked()
    {
        // Container contract remains: only MP4/MOV. A WebM container is still rejected even though
        // its codecs (vp9/opus) are no longer inspected for Facebook Story.
        var path = SeedRawMedia("v-fb-story-webm", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-story-webm"] = path },
            FakeVideo(1080, 1920, 10, container: "webm", videoCodec: "vp9", audioCodec: "opus"));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-webm", MediaType.Video, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedContainer);
    }

    [Fact]
    public async Task Video_FacebookStory_UnreadableMetadata_IsStillBlocked()
    {
        // Readability contract remains: if metadata cannot be extracted, the video is rejected.
        var path = SeedRawMedia("v-fb-story-corrupt", "video/mp4", 10L * 1024 * 1024);
        var nullExtractor = new Mock<IVideoMetadataExtractor>();
        nullExtractor.Setup(e => e.ExtractAsync(It.IsAny<string>())).ReturnsAsync((VideoMetadata?)null);
        var gate = CreateGate(new() { ["v-fb-story-corrupt"] = path }, nullExtractor.Object);

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-corrupt", MediaType.Video, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.MetadataExtractionFailed);
    }

    [Theory]
    [InlineData("prores", "aac")]
    [InlineData("vp9", "opus")]
    public async Task Video_Codec_RejectedForFacebookFeed_ButAcceptedForInstagramFeedAndFacebookStory(string videoCodec, string audioCodec)
    {
        // Scope check: codec rules remain ONLY for Facebook Feed. The finalized IG Feed policy and
        // Facebook Story both delegate codec playability to Meta, so the SAME uncommon codec that
        // Facebook Feed rejects is accepted for Instagram Feed and Facebook Story. 1080x1920 @ 10s
        // is within every other rule, so codec is the only variable.
        var key = $"v-xplace-{videoCodec}-{audioCodec}";
        var path = SeedRawMedia(key, "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path }, FakeVideo(1080, 1920, 10, videoCodec: videoCodec, audioCodec: audioCodec));
        var item = new[] { new MediaGateItem(key, MediaType.Video, 0) };

        var fb = await gate.ValidateAsync(Ws, item, new[] { Fb });
        Assert.False(fb.IsValid);
        Assert.Contains(fb.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedVideoCodec);

        var ig = await gate.ValidateAsync(Ws, item, new[] { Ig });
        Assert.True(ig.IsValid); // IG Feed no longer validates codec

        var fbStory = await gate.ValidateAsync(Ws, item, new[] { FbStory });
        Assert.True(fbStory.IsValid);
    }

    // Regression: an Instagram Feed video is NOT rejected for codec, audio codec, frame rate,
    // width, height, or aspect ratio — only container/type, size, duration, and readability.
    // Each row below would have failed under the old IG Feed rules (ProRes/VP9 codec, non-AAC
    // audio, out-of-range fps, sub-500 or huge dimensions, extreme aspect) but must now pass.
    [Theory]
    [InlineData("prores", "aac", 30, 1080, 1920)]   // ProRes was UNSUPPORTED_VIDEO_CODEC
    [InlineData("vp9", "opus", 30, 1080, 1920)]      // VP9 + Opus were both unsupported
    [InlineData("h264", "mp3", 30, 1080, 1920)]      // non-AAC audio was UNSUPPORTED_AUDIO_CODEC
    [InlineData("h264", "aac", 12, 1080, 1920)]      // 12fps was below the old 23fps floor
    [InlineData("h264", "aac", 120, 1080, 1920)]     // 120fps was above the old 60fps ceiling
    [InlineData("h264", "aac", 30, 200, 200)]        // below the old 500x500 minimum
    [InlineData("h264", "aac", 30, 3840, 2160)]      // above the old 1920 maximum, wide 16:9
    [InlineData("h264", "aac", 30, 1080, 300)]       // 3.6 aspect, far outside the old 0.5625–1.91
    public async Task Video_InstagramFeed_NotRejectedForCodecFpsDimensionsOrAspect(
        string? videoCodec, string? audioCodec, double fps, int width, int height)
    {
        var key = $"v-ig-relaxed-{videoCodec}-{audioCodec}-{fps}-{width}x{height}";
        var path = SeedRawMedia(key, "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { [key] = path },
            FakeVideo(width, height, 30, videoCodec: videoCodec, audioCodec: audioCodec, fps: fps));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Video, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_FacebookStory_Over50MB_IsBlocked_WithMbMessage()
    {
        // Size contract: one byte over the 50MB (52,428,800 bytes) Story cap is blocked, and the
        // error surfaces the human "50MB" limit — never a raw byte count.
        var path = SeedRawMedia("v-fb-story-big", "video/mp4", 52_428_801L);
        var gate = CreateGate(new() { ["v-fb-story-big"] = path }, FakeVideo(1080, 1920, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-big", MediaType.Video, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge
            && e.Message == "This video is too large. Facebook videos can be up to 50MB."
            && !e.Message.Contains("52428800") && !e.Message.Contains("52,428,800"));
    }

    [Fact]
    public async Task Video_FacebookStory_AtExactly50MB_IsAccepted()
    {
        // Inclusive boundary: exactly 52,428,800 bytes passes (backend rejects only size > max).
        var path = SeedRawMedia("v-fb-story-at-50", "video/mp4", 52_428_800L);
        var gate = CreateGate(new() { ["v-fb-story-at-50"] = path }, FakeVideo(1080, 1920, 10));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-story-at-50", MediaType.Video, 0) }, new[] { FbStory });

        Assert.True(result.IsValid);
    }

    // ── FB Story IMAGE: any shape/size within the type + 10MB limit is accepted ──

    [Theory]
    [InlineData(1080, 1080)] // square (was blocked: aspect 1.0)
    [InlineData(1920, 1080)] // landscape (was blocked)
    [InlineData(3000, 300)]  // extremely wide
    [InlineData(300, 3000)]  // extremely tall
    [InlineData(100, 100)]   // below the old 320x320 minimum
    [InlineData(4000, 6000)] // above the old 1080x1920 maximum
    public async Task Image_FacebookStory_AnyDimensionsOrAspect_IsAccepted(int width, int height)
    {
        var key = $"i-fb-story-{width}x{height}";
        var path = SeedMedia(key, "image/jpeg", "jpeg", width, height);
        var gate = CreateGate(new() { [key] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem(key, MediaType.Image, 0) }, new[] { FbStory });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Image_FacebookStory_UnsupportedType_IsStillBlocked()
    {
        // Type contract remains: WebP is decoded as image/webp and rejected (JPG/PNG only).
        var path = SeedMedia("i-fb-story-webp", "image/webp", "webp", 1080, 1920);
        var gate = CreateGate(new() { ["i-fb-story-webp"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("i-fb-story-webp", MediaType.Image, 0) }, new[] { FbStory });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedMimeType);
    }

    [Fact]
    public async Task Video_FacebookFeed_Over50MB_IsBlocked_NotOneGB()
    {
        // FB feed video cap is the product 50MB ceiling (52,428,800 bytes, the Supabase
        // Free global upload limit), NOT Meta's 1GB API limit. One byte over is blocked.
        var path = SeedRawMedia("v-fb-over-cap", "video/mp4", 52_428_801L);
        var gate = CreateGate(new() { ["v-fb-over-cap"] = path }, FakeVideo(1280, 720, 30));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-over-cap", MediaType.Video, 0) }, new[] { Fb });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge);
    }

    // ── FB feed video duration: 3–180s MVP cap (was 240 minutes) ────────────────

    [Fact]
    public async Task Video_FacebookFeed_180Seconds_Passes()
    {
        var path = SeedRawMedia("v-fb-180", "video/mp4", 50L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-180"] = path }, FakeVideo(1280, 720, 180));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-180", MediaType.Video, 0) }, new[] { Fb });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_FacebookFeed_181Seconds_IsBlocked()
    {
        var path = SeedRawMedia("v-fb-181", "video/mp4", 50L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-181"] = path }, FakeVideo(1280, 720, 181));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-181", MediaType.Video, 0) }, new[] { Fb });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong
            && e.Message == "Feed videos must be between 3 and 180 seconds.");
    }

    [Fact]
    public async Task Video_FacebookFeed_HourLong_PreviouslyAllowed_NowBlocked()
    {
        // The old rule allowed up to 240 minutes; the MVP cap is 180 seconds. Size stays
        // within the 50MB cap so the only expected error is the duration.
        var path = SeedRawMedia("v-fb-hour", "video/mp4", 20L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-hour"] = path }, FakeVideo(1280, 720, 60 * 60));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-hour", MediaType.Video, 0) }, new[] { Fb });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
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
    public async Task Video_Mov_UnsupportedCodec_IsBlocked_ForFacebookFeed()
    {
        // MOV is accepted for iPhone compatibility, but an unsupported internal codec (e.g.
        // ProRes) still fails for Facebook Feed, which keeps its codec allow-list. (Instagram
        // Feed no longer validates codec — see Video_InstagramFeed_NotRejectedForCodec... .)
        var path = SeedRawMedia("v-fb-prores", "video/quicktime", 20L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-prores"] = path },
            FakeVideo(1280, 720, 10, container: "mov", videoCodec: "prores", audioCodec: "aac"));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-prores", MediaType.Video, 0) }, new[] { Fb });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedVideoCodec);
    }

    [Fact]
    public async Task Video_Mov_UnsupportedAudio_IsBlocked_ForFacebookFeed()
    {
        var path = SeedRawMedia("v-fb-pcm", "video/quicktime", 20L * 1024 * 1024);
        var gate = CreateGate(new() { ["v-fb-pcm"] = path },
            FakeVideo(1280, 720, 10, container: "mov", videoCodec: "h264", audioCodec: "pcm_s16le"));

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("v-fb-pcm", MediaType.Video, 0) }, new[] { Fb });

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
        var gate = CreateGate(new() { ["d-vid-long"] = path }, FakeVideo(1080, 1080, 181)); // > 180s MVP cap

        var result = await gate.ValidateForDisplayAsync(Ws,
            new MediaGateItem("d-vid-long", MediaType.Video, 0), Ig);

        Assert.Equal(ValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    [Fact]
    public async Task ValidateForDisplay_InstagramFeedVideoOver50MB_IsInvalid_FileTooLarge()
    {
        var path = SeedRawMedia("d-ig-vid-over-50", "video/mp4", 52_428_801L);
        var gate = CreateGate(new() { ["d-ig-vid-over-50"] = path }, FakeVideo(1080, 1080, 30));

        var result = await gate.ValidateForDisplayAsync(Ws,
            new MediaGateItem("d-ig-vid-over-50", MediaType.Video, 0), Ig);

        Assert.Equal(ValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge
                                          && e.Message.Contains("50MB")
                                          && !e.Message.Contains("100MB"));
    }

    [Fact]
    public async Task ValidateForDisplay_FacebookFeedVideoOver50MB_IsInvalid_FileTooLarge()
    {
        // Advisory path for the composer card: a stored FB Feed video one byte over the
        // 50MB (52,428,800 bytes) product cap must come back as blocking Invalid.
        var path = SeedRawMedia("d-vid-over-50", "video/mp4", 52_428_801L);
        var gate = CreateGate(new() { ["d-vid-over-50"] = path }, FakeVideo(1280, 720, 30));

        var result = await gate.ValidateForDisplayAsync(Ws,
            new MediaGateItem("d-vid-over-50", MediaType.Video, 0), Fb);

        Assert.Equal(ValidationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge
                                          && e.Message.Contains("50MB"));
    }

    [Fact]
    public async Task ValidateForDisplay_FacebookFeedVideoAtExactly50MB_IsNotInvalid()
    {
        var path = SeedRawMedia("d-vid-at-50", "video/mp4", 52_428_800L); // inclusive boundary
        var gate = CreateGate(new() { ["d-vid-at-50"] = path }, FakeVideo(1280, 720, 30));

        var result = await gate.ValidateForDisplayAsync(Ws,
            new MediaGateItem("d-vid-at-50", MediaType.Video, 0), Fb);

        Assert.NotEqual(ValidationStatus.Invalid, result.Status);
    }

    // ── Instagram Feed carousel VIDEO: 3–60s per item (vs 180s single) ──────────
    // A post with 2+ items is a carousel; its video items use the 60s cap. These drive the gate
    // exactly as create/publish do (all items in one ValidateAsync call).

    [Theory]
    [InlineData(3)]  // inclusive lower boundary
    [InlineData(30)] // mid-range
    [InlineData(60)] // inclusive upper boundary — allowed in a carousel
    public async Task Video_InstagramFeedCarousel_WithinDurationRange_Passes(double durationSeconds)
    {
        // Two video items → carousel. Both share the same duration via the uniform fake.
        var a = SeedRawMedia("vc-a", "video/mp4", 10L * 1024 * 1024);
        var b = SeedRawMedia("vc-b", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["vc-a"] = a, ["vc-b"] = b }, FakeVideo(1080, 1080, durationSeconds));

        var result = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("vc-a", MediaType.Video, 0),
            new MediaGateItem("vc-b", MediaType.Video, 1),
        }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Video_InstagramFeedCarousel_90s_IsBlocked_EvenThoughValidAsSingle()
    {
        // 90s is valid for a SINGLE Feed video (≤180s) but too long for a carousel item (≤60s).
        var a = SeedRawMedia("vc90-a", "video/mp4", 10L * 1024 * 1024);
        var b = SeedRawMedia("vc90-b", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["vc90-a"] = a, ["vc90-b"] = b }, FakeVideo(1080, 1080, 90));

        // Single item (not a carousel) at 90s → accepted.
        var single = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("vc90-a", MediaType.Video, 0) }, new[] { Ig });
        Assert.True(single.IsValid);

        // Same 90s video inside a 2-item carousel → rejected with the carousel-specific message.
        var carousel = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("vc90-a", MediaType.Video, 0),
            new MediaGateItem("vc90-b", MediaType.Video, 1),
        }, new[] { Ig });

        Assert.False(carousel.IsValid);
        Assert.Contains(carousel.Errors, e =>
            e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong
            && e.Message == "Videos in an Instagram Feed carousel must be between 3 and 60 seconds.");
    }

    [Fact]
    public async Task Video_InstagramFeedCarousel_61s_IsBlocked()
    {
        var a = SeedRawMedia("vc61-a", "video/mp4", 10L * 1024 * 1024);
        var b = SeedRawMedia("vc61-b", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["vc61-a"] = a, ["vc61-b"] = b }, FakeVideo(1080, 1080, 61));

        var result = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("vc61-a", MediaType.Video, 0),
            new MediaGateItem("vc61-b", MediaType.Video, 1),
        }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    [Fact]
    public async Task Video_InstagramFeedCarousel_TooShort_IsBlocked()
    {
        var a = SeedRawMedia("vc2-a", "video/mp4", 10L * 1024 * 1024);
        var b = SeedRawMedia("vc2-b", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["vc2-a"] = a, ["vc2-b"] = b }, FakeVideo(1080, 1080, 2));

        var result = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("vc2-a", MediaType.Video, 0),
            new MediaGateItem("vc2-b", MediaType.Video, 1),
        }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooShort);
    }

    [Fact]
    public async Task Carousel_VideoOnly_AllWithinCarouselLimits_IsAccepted()
    {
        var a = SeedRawMedia("vco-a", "video/mp4", 10L * 1024 * 1024);
        var b = SeedRawMedia("vco-b", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["vco-a"] = a, ["vco-b"] = b }, FakeVideo(1080, 1080, 45));

        var result = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("vco-a", MediaType.Video, 0),
            new MediaGateItem("vco-b", MediaType.Video, 1),
        }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Carousel_Mixed_ImageAndVideo_IsAccepted()
    {
        // 1 JPEG image + 1 40s video → mixed carousel, both valid. Image uses the image extractor;
        // the video uses the (60s) carousel video rule.
        var img = SeedMedia("mx-img", "image/jpeg", "jpeg", 1080, 1080);
        var vid = SeedRawMedia("mx-vid", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(new() { ["mx-img"] = img, ["mx-vid"] = vid }, FakeVideo(1080, 1080, 40));

        var result = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("mx-img", MediaType.Image, 0),
            new MediaGateItem("mx-vid", MediaType.Video, 1),
        }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Carousel_ImageOnly_IsAccepted()
    {
        var a = SeedMedia("ic-a", "image/jpeg", "jpeg", 1080, 1080);
        var b = SeedMedia("ic-b", "image/jpeg", "jpeg", 1080, 1350); // 4:5
        var gate = CreateGate(new() { ["ic-a"] = a, ["ic-b"] = b });

        var result = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("ic-a", MediaType.Image, 0),
            new MediaGateItem("ic-b", MediaType.Image, 1),
        }, new[] { Ig });

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task Carousel_InvalidChildVideo_RejectsWholeCarousel_AndIdentifiesItem()
    {
        // Item order 0 is a valid 30s video; order 1 is a 200s video (invalid even as a carousel
        // item). The carousel is rejected and ONLY the offending item (order 1) is reported —
        // proving invalid children fail the whole carousel and item order is preserved.
        var good = SeedRawMedia("cc-good", "video/mp4", 10L * 1024 * 1024);
        var bad = SeedRawMedia("cc-bad", "video/mp4", 10L * 1024 * 1024);
        var gate = CreateGate(
            new() { ["cc-good"] = good, ["cc-bad"] = bad },
            FakeVideoByPath(new() { [good] = Vid(30), [bad] = Vid(200) }));

        var result = await gate.ValidateAsync(Ws, new[]
        {
            new MediaGateItem("cc-good", MediaType.Video, 0),
            new MediaGateItem("cc-bad", MediaType.Video, 1),
        }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.All(result.Errors, e => Assert.Equal(1, e.Order));
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.DurationTooLong);
    }

    [Fact]
    public async Task Carousel_TenItems_IsAccepted()
    {
        // The gate itself imposes no item-count ceiling (the 2–10 count check lives in the
        // create/update controller); 10 valid items validate cleanly here.
        var keyToPath = new Dictionary<string, string>();
        var items = new List<MediaGateItem>();
        for (var i = 0; i < 10; i++)
        {
            var key = $"ten-{i}";
            keyToPath[key] = SeedMedia(key, "image/jpeg", "jpeg", 1080, 1080);
            items.Add(new MediaGateItem(key, MediaType.Image, i));
        }
        var gate = CreateGate(keyToPath);

        var result = await gate.ValidateAsync(Ws, items, new[] { Ig });

        Assert.True(result.IsValid);
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
