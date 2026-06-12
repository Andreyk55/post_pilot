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

    private MediaValidationGate CreateGate(Dictionary<string, string> keyToPath)
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
            Mock.Of<IVideoMetadataExtractor>(),
            NullLogger<MediaValidationService>.Instance);

        return new MediaValidationGate(
            _db, mediaService.Object, validationService, NullLogger<MediaValidationGate>.Instance);
    }

    private static MediaGateTarget Fb => new(Platform.Facebook, Placement.Feed);
    private static MediaGateTarget Ig => new(Platform.Instagram, Placement.Feed);

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
    public async Task Png_Instagram_IsBlocked()
    {
        var path = SeedMedia("k-ig-png", "image/png", "png", 1080, 1080);
        var gate = CreateGate(new() { ["k-ig-png"] = path });

        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-ig-png", MediaType.Image, 0) }, new[] { Ig });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedMimeType
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
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.UnsupportedMimeType);
    }

    // ── Pass-through cases ──────────────────────────────────────────────────────

    [Fact]
    public async Task VideoItem_IsPassedThrough_NotValidated()
    {
        // No Media row / file needed: videos are skipped entirely in this phase.
        var gate = CreateGate(new());
        var result = await gate.ValidateAsync(Ws,
            new[] { new MediaGateItem("k-video", MediaType.Video, 0) }, new[] { Ig });

        Assert.True(result.IsValid);
    }

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
