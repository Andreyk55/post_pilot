using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Services.Validation;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Phase 3 tests: the media validation gate and Instagram publisher use the stored
/// Instagram JPEG derivative for PNG originals (not the raw PNG), while Facebook keeps
/// validating/publishing the original PNG. Uses the REAL <see cref="MediaValidationGate"/>
/// with real ImageSharp metadata extraction, and a fake media service that maps storage
/// keys (original AND derivative) to on-disk image files.
/// </summary>
public class InstagramDerivativeGateAndPublisherTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000d3");

    private readonly AppDbContext _db;
    private readonly List<string> _tempFiles = new();
    private readonly Dictionary<string, string> _keyToPath = new();

    public InstagramDerivativeGateAndPublisherTests()
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

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private string WriteImage(string format, int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        var ext = format == "png" ? ".png" : ".jpg";
        var path = Path.Combine(Path.GetTempPath(), $"derivtest_{Guid.NewGuid():N}{ext}");
        using (var fs = File.Create(path))
        {
            if (format == "png") image.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            else image.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        }
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Seeds a PNG original Media row, optionally with a JPEG derivative. Both keys map to
    /// real image files so the gate can decode them.
    /// </summary>
    private Media SeedPngMedia(
        string originalKey, int width, int height,
        string? derivativeKey = null, int derivWidth = 0, int derivHeight = 0)
    {
        var originalPath = WriteImage("png", width, height);
        _keyToPath[originalKey] = originalPath;

        var media = new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = originalKey, OriginalFileName = "photo.png", ContentType = "image/png",
            SizeBytes = new FileInfo(originalPath).Length, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        };

        if (derivativeKey != null)
        {
            var derivPath = WriteImage("jpeg", derivWidth, derivHeight);
            _keyToPath[derivativeKey] = derivPath;
            media.InstagramImageStorageKey = derivativeKey;
            media.InstagramImageMimeType = "image/jpeg";
            media.InstagramImageSizeBytes = new FileInfo(derivPath).Length;
            media.InstagramImageWidth = derivWidth;
            media.InstagramImageHeight = derivHeight;
            media.InstagramImageGeneratedAt = DateTime.UtcNow;
        }

        _db.Media.Add(media);
        _db.SaveChanges();
        return media;
    }

    private IMediaService BuildMediaService()
    {
        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>()))
            .Returns<string>(key => Task.FromResult<string?>(_keyToPath.TryGetValue(key, out var p) ? p : null));
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>()));
        mediaService.Setup(m => m.GetPublishingUrlAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns<string, TimeSpan?, CancellationToken>((key, _, _) => Task.FromResult($"https://signed.example/{key}?token=SECRET"));
        return mediaService.Object;
    }

    private MediaValidationGate BuildGate(IMediaService mediaService) =>
        new(_db, mediaService,
            new MediaValidationService(
                new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
                Mock.Of<IVideoMetadataExtractor>(),
                NullLogger<MediaValidationService>.Instance),
            NullLogger<MediaValidationGate>.Instance);

    // ── Gate: PNG + valid derivative passes for Instagram ───────────────────────

    [Fact]
    public async Task Gate_InstagramPng_WithValidDerivative_Passes()
    {
        SeedPngMedia("png-orig", 2000, 2000, derivativeKey: "png-orig.ig.jpg", derivWidth: 1440, derivHeight: 1440);

        var gate = BuildGate(BuildMediaService());
        var result = await gate.ValidateAsync(
            Ws,
            new[] { new MediaGateItem("png-orig", MediaType.Image, 0) },
            new[] { new MediaGateTarget(Platform.Instagram, Placement.Feed) });

        Assert.True(result.IsValid);
    }

    // ── Gate: PNG without derivative blocked clearly for Instagram ──────────────

    [Fact]
    public async Task Gate_InstagramPng_WithoutDerivative_IsBlocked_WithClearCode()
    {
        SeedPngMedia("png-no-deriv", 1080, 1080); // no derivative

        var gate = BuildGate(BuildMediaService());
        var result = await gate.ValidateAsync(
            Ws,
            new[] { new MediaGateItem("png-no-deriv", MediaType.Image, 0) },
            new[] { new MediaGateTarget(Platform.Instagram, Placement.Feed) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.InstagramDerivativeMissing);
    }

    // ── Gate: PNG derivative with bad aspect ratio still blocks ─────────────────

    [Fact]
    public async Task Gate_InstagramPng_DerivativeBadAspect_IsBlocked()
    {
        // Derivative is JPEG but 4:1 — outside IG's allowed range. Conversion may have
        // happened, but the gate must still reject it for Instagram.
        SeedPngMedia("png-bad-aspect", 1600, 400, derivativeKey: "png-bad-aspect.ig.jpg", derivWidth: 1440, derivHeight: 360);

        var gate = BuildGate(BuildMediaService());
        var result = await gate.ValidateAsync(
            Ws,
            new[] { new MediaGateItem("png-bad-aspect", MediaType.Image, 0) },
            new[] { new MediaGateTarget(Platform.Instagram, Placement.Feed) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.AspectRatioInvalid);
    }

    // ── Gate: too-large derivative blocks ───────────────────────────────────────

    [Fact]
    public async Task Gate_InstagramPng_OversizeDerivative_IsBlocked()
    {
        // Seed a derivative file that is genuinely > 8MB by making a real large JPEG.
        var originalPath = WriteImage("png", 1080, 1080);
        _keyToPath["png-big"] = originalPath;

        // Build a >8MB JPEG by encoding random noise at a large size.
        using var noise = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4000, 4000);
        var rnd = new Random(1);
        noise.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
            }
        });
        var bigPath = Path.Combine(Path.GetTempPath(), $"derivtest_big_{Guid.NewGuid():N}.jpg");
        using (var fs = File.Create(bigPath))
            noise.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 100 });
        _tempFiles.Add(bigPath);
        _keyToPath["png-big.ig.jpg"] = bigPath;

        Assert.True(new FileInfo(bigPath).Length > 8L * 1024 * 1024, "test fixture must exceed IG's 8MB limit");

        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = "png-big", OriginalFileName = "photo.png", ContentType = "image/png",
            SizeBytes = new FileInfo(originalPath).Length, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
            InstagramImageStorageKey = "png-big.ig.jpg", InstagramImageMimeType = "image/jpeg",
            InstagramImageSizeBytes = new FileInfo(bigPath).Length,
            InstagramImageWidth = 4000, InstagramImageHeight = 4000,
            InstagramImageGeneratedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var gate = BuildGate(BuildMediaService());
        var result = await gate.ValidateAsync(
            Ws,
            new[] { new MediaGateItem("png-big", MediaType.Image, 0) },
            new[] { new MediaGateTarget(Platform.Instagram, Placement.Feed) });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Code == DTOs.MediaValidationErrorCodes.FileTooLarge);
    }

    // ── Gate: Facebook still validates the ORIGINAL PNG (accepted) ──────────────

    [Fact]
    public async Task Gate_FacebookPng_ValidatesOriginal_AndPasses()
    {
        // PNG original within FB limits; has NO derivative. Facebook must validate the
        // original and accept it (PNG is allowed on Facebook).
        SeedPngMedia("fb-png", 1200, 630);

        var gate = BuildGate(BuildMediaService());
        var result = await gate.ValidateAsync(
            Ws,
            new[] { new MediaGateItem("fb-png", MediaType.Image, 0) },
            new[] { new MediaGateTarget(Platform.Facebook, Placement.Feed) });

        Assert.True(result.IsValid);
    }

    // ── Publisher: IG resolves the DERIVATIVE key, not the PNG ──────────────────

    [Fact]
    public async Task InstagramKeyResolver_Png_ResolvesDerivativeKey()
    {
        SeedPngMedia("ig-png", 2000, 2000, derivativeKey: "ig-png.ig.jpg", derivWidth: 1440, derivHeight: 1440);

        var resolved = await InstagramMediaKeyResolver.ResolveAsync(
            _db, BuildMediaService(), Ws, "ig-png", CancellationToken.None);

        Assert.Equal("ig-png.ig.jpg", resolved);
    }

    [Fact]
    public async Task InstagramKeyResolver_Jpeg_ResolvesOriginalKey()
    {
        // A JPEG original has no derivative — publish the original as-is.
        var jpegPath = WriteImage("jpeg", 1080, 1080);
        _keyToPath["ig-jpg"] = jpegPath;
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = "ig-jpg", OriginalFileName = "photo.jpg", ContentType = "image/jpeg",
            SizeBytes = new FileInfo(jpegPath).Length, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        var resolved = await InstagramMediaKeyResolver.ResolveAsync(
            _db, BuildMediaService(), Ws, "ig-jpg", CancellationToken.None);

        Assert.Equal("ig-jpg", resolved);
    }

    [Fact]
    public async Task InstagramKeyResolver_PngWithoutDerivative_Throws()
    {
        SeedPngMedia("ig-png-missing", 1080, 1080); // no derivative

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InstagramMediaKeyResolver.ResolveAsync(
                _db, BuildMediaService(), Ws, "ig-png-missing", CancellationToken.None));
    }
}
