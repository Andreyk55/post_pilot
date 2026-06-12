using Xunit;
using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Validation;

namespace PostPilot.Api.Tests;

public class MediaValidationTests
{
    // Test rule retrieval
    [Fact]
    public void GetRules_FacebookFeedImage_ReturnsRules()
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Image);

        Assert.NotNull(rules);
        Assert.Contains("image/jpeg", rules.AllowedMimeTypes);
        Assert.Contains("image/png", rules.AllowedMimeTypes);
        Assert.Equal(4L * 1024 * 1024, rules.MaxBytes); // 4MB
        Assert.Equal(320, rules.MinWidth);
        Assert.Equal(320, rules.MinHeight);
    }

    [Fact]
    public void GetRules_FacebookFeedVideo_ReturnsRules()
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Video);

        Assert.NotNull(rules);
        Assert.Contains("video/mp4", rules.AllowedMimeTypes);
        Assert.Equal(1024L * 1024 * 1024, rules.MaxBytes); // 1GB
        Assert.Equal(1, rules.DurationMinSeconds);
        Assert.Equal(240 * 60, rules.DurationMaxSeconds); // 240 minutes
    }

    [Fact]
    public void GetRules_UndefinedCombination_ReturnsNull()
    {
        // LinkedIn Story is not a defined placement (LinkedIn only has Feed).
        // Originally this test used Facebook Story, but Facebook stories
        // were added later — keep the assertion meaningful by picking a
        // combination that is actually undefined.
        var rules = MediaValidationRules.GetRules(Platform.LinkedIn, Placement.Story, MediaType.Image);

        Assert.Null(rules);
    }

    [Fact]
    public void HasRules_ExistingCombination_ReturnsTrue()
    {
        Assert.True(MediaValidationRules.HasRules(Platform.Facebook, Placement.Feed, MediaType.Image));
        Assert.True(MediaValidationRules.HasRules(Platform.Facebook, Placement.Feed, MediaType.Video));
    }

    [Fact]
    public void HasRules_NonExistingCombination_ReturnsFalse()
    {
        // LinkedIn Story placement not defined.
        Assert.False(MediaValidationRules.HasRules(Platform.LinkedIn, Placement.Story, MediaType.Image));
    }
}

public class MediaValidationRulesEvaluationTests
{
    // These tests validate the rule evaluation logic

    [Theory]
    [InlineData(5L * 1024 * 1024, true)] // 5MB > 4MB limit
    [InlineData(4L * 1024 * 1024, false)] // Exactly at limit
    [InlineData(1L * 1024 * 1024, false)] // Under limit
    public void FileTooLarge_FacebookImage_ValidatesCorrectly(long sizeBytes, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Image)!;
        var isTooLarge = sizeBytes > rules.MaxBytes;

        Assert.Equal(shouldFail, isTooLarge);
    }

    [Theory]
    [InlineData("image/jpeg", false)]
    [InlineData("image/png", false)]
    [InlineData("image/gif", false)]
    [InlineData("image/svg+xml", true)] // SVG not supported
    [InlineData("application/pdf", true)]
    public void UnsupportedMimeType_FacebookImage_ValidatesCorrectly(string mimeType, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Image)!;
        var isUnsupported = !rules.AllowedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(shouldFail, isUnsupported);
    }

    [Theory]
    [InlineData(200, 200, true)] // Too small
    [InlineData(320, 320, false)] // Exactly at minimum
    [InlineData(1200, 630, false)] // Recommended size
    [InlineData(3000, 3000, true)] // Too large (over 2048 max)
    public void Dimensions_FacebookImage_ValidatesCorrectly(int width, int height, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Image)!;

        var tooSmall = width < rules.MinWidth || height < rules.MinHeight;
        var tooLarge = width > rules.MaxWidth || height > rules.MaxHeight;
        var isInvalid = tooSmall || tooLarge;

        Assert.Equal(shouldFail, isInvalid);
    }

    [Theory]
    [InlineData(1920, 1080, false)] // 16:9 = 1.78 - valid
    [InlineData(1080, 1080, false)] // 1:1 = 1.0 - valid
    [InlineData(1080, 1920, false)] // 9:16 = 0.5625 - valid (minimum)
    [InlineData(1000, 2000, true)] // 0.5 - too narrow
    [InlineData(2000, 1000, true)] // 2.0 - too wide (max is 1.91)
    public void AspectRatio_FacebookImage_ValidatesCorrectly(int width, int height, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Image)!;

        var aspectRatio = (double)width / height;
        var isInvalid = aspectRatio < rules.AspectRatioMin || aspectRatio > rules.AspectRatioMax;

        Assert.Equal(shouldFail, isInvalid);
    }

    [Theory]
    [InlineData(0.5, true)] // Too short
    [InlineData(1, false)] // Exactly minimum
    [InlineData(60, false)] // 1 minute
    [InlineData(240 * 60, false)] // 4 hours - exactly at max
    [InlineData(240 * 60 + 1, true)] // Over 4 hours
    public void VideoDuration_FacebookFeedVideo_ValidatesCorrectly(double durationSeconds, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Video)!;

        var tooShort = rules.DurationMinSeconds.HasValue && durationSeconds < rules.DurationMinSeconds.Value;
        var tooLong = rules.DurationMaxSeconds.HasValue && durationSeconds > rules.DurationMaxSeconds.Value;
        var isInvalid = tooShort || tooLong;

        Assert.Equal(shouldFail, isInvalid);
    }
}

public class MediaValidationErrorCodesTests
{
    [Fact]
    public void ErrorCodes_AreCorrectlyDefined()
    {
        Assert.Equal("FILE_TOO_LARGE", MediaValidationErrorCodes.FileTooLarge);
        Assert.Equal("UNSUPPORTED_MIME_TYPE", MediaValidationErrorCodes.UnsupportedMimeType);
        Assert.Equal("DIMENSIONS_TOO_SMALL", MediaValidationErrorCodes.DimensionsTooSmall);
        Assert.Equal("DIMENSIONS_TOO_LARGE", MediaValidationErrorCodes.DimensionsTooLarge);
        Assert.Equal("ASPECT_RATIO_INVALID", MediaValidationErrorCodes.AspectRatioInvalid);
        Assert.Equal("DURATION_TOO_SHORT", MediaValidationErrorCodes.DurationTooShort);
        Assert.Equal("DURATION_TOO_LONG", MediaValidationErrorCodes.DurationTooLong);
    }
}

public class InstagramValidationRulesTests
{
    [Fact]
    public void GetRules_InstagramFeedImage_ReturnsCorrectLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Image);

        Assert.NotNull(rules);
        Assert.Equal(8L * 1024 * 1024, rules.MaxBytes); // 8MB
        Assert.Equal(320, rules.MinWidth);
        Assert.Equal(1440, rules.MaxWidth);
        Assert.Equal(0.8, rules.AspectRatioMin); // 4:5
        Assert.Equal(1.91, rules.AspectRatioMax);
    }

    // Meta accepts JPEG ONLY for Instagram. PNG/WebP that "pass" locally would be
    // rejected by Meta at publish time, so they must be invalid here too.
    [Fact]
    public void GetRules_InstagramFeedImage_IsJpegOnly()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Image)!;

        Assert.Equal(new[] { "image/jpeg" }, rules.AllowedMimeTypes);
        Assert.DoesNotContain("image/png", rules.AllowedMimeTypes);
        Assert.DoesNotContain("image/webp", rules.AllowedMimeTypes);
    }

    [Fact]
    public void GetRules_InstagramStoryImage_IsJpegOnly()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Story, MediaType.Image)!;

        Assert.Equal(new[] { "image/jpeg" }, rules.AllowedMimeTypes);
        Assert.DoesNotContain("image/png", rules.AllowedMimeTypes);
        Assert.DoesNotContain("image/webp", rules.AllowedMimeTypes);
    }

    // Instagram carousels reuse the IG Feed Image rule (the validate call always passes
    // placement=Feed for carousel items), so JPEG-only coverage there protects carousels too.
    [Theory]
    [InlineData("image/jpeg", false)] // JPEG is the only accepted IG image format
    [InlineData("image/png", true)]   // PNG rejected by Meta for Instagram
    [InlineData("image/webp", true)]  // WebP rejected by Meta for Instagram
    [InlineData("image/gif", true)]
    public void MimeType_InstagramFeedImage_JpegOnly(string mimeType, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Image)!;
        var isUnsupported = !rules.AllowedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(shouldFail, isUnsupported);
    }

    // Facebook keeps PNG (Meta accepts JPEG/PNG/BMP/GIF/TIFF for Page photos), so the same
    // PNG that is invalid for Instagram must remain valid for Facebook.
    [Fact]
    public void MimeType_PngValidForFacebook_InvalidForInstagram()
    {
        var fbRules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Image)!;
        var igRules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Image)!;

        Assert.Contains("image/png", fbRules.AllowedMimeTypes);
        Assert.DoesNotContain("image/png", igRules.AllowedMimeTypes);
    }

    // Instagram max width (1440) is advisory: Meta downscales rather than rejecting,
    // so the rule must be flagged so the service warns instead of erroring.
    [Fact]
    public void InstagramFeedImage_MaxWidthIsAdvisory()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Image)!;
        Assert.True(rules.MaxWidthIsAdvisory);
    }

    // Facebook max dimensions stay a hard limit (not advisory).
    [Fact]
    public void FacebookFeedImage_MaxWidthIsHardLimit()
    {
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Feed, MediaType.Image)!;
        Assert.False(rules.MaxWidthIsAdvisory);
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_ReturnsCorrectDurationLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video);

        Assert.NotNull(rules);
        Assert.Equal(3, rules.DurationMinSeconds);
        Assert.Equal(60, rules.DurationMaxSeconds); // 60 seconds max for feed
    }
}

public class TwitterValidationRulesTests
{
    [Fact]
    public void GetRules_TwitterFeedVideo_ReturnsCorrectDurationLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Twitter, Placement.Feed, MediaType.Video);

        Assert.NotNull(rules);
        Assert.Equal(0.5, rules.DurationMinSeconds);
        Assert.Equal(140, rules.DurationMaxSeconds); // 2 minutes 20 seconds
        Assert.Equal(512L * 1024 * 1024, rules.MaxBytes); // 512MB
    }
}

public class LinkedInValidationRulesTests
{
    [Fact]
    public void GetRules_LinkedInFeedVideo_ReturnsCorrectLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.LinkedIn, Placement.Feed, MediaType.Video);

        Assert.NotNull(rules);
        Assert.Equal(3, rules.DurationMinSeconds);
        Assert.Equal(600, rules.DurationMaxSeconds); // 10 minutes
        Assert.Equal(200L * 1024 * 1024, rules.MaxBytes); // 200MB
    }
}

/// <summary>
/// End-to-end behavioral tests that drive the full <see cref="MediaValidationService"/>
/// (including the real ImageSharp byte-level format detection) with generated image files.
/// These prove the Phase 1 correctness fixes actually take effect, not just the rule data:
///   - PNG/WebP are rejected for Instagram (JPEG only) but PNG stays valid for Facebook.
///   - JPEG within size/aspect passes for Instagram.
///   - Over-wide Instagram images warn (Meta downscales) rather than hard-failing.
/// </summary>
public class MediaValidationServiceImageBehaviorTests
{
    private static MediaValidationService CreateService() =>
        new(
            new ImageMetadataExtractor(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageMetadataExtractor>.Instance),
            new ThrowingVideoExtractor(), // images never touch the video extractor
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MediaValidationService>.Instance);

    /// <summary>
    /// Writes a real encoded image of the given format/size to a temp file and returns its path.
    /// Caller is responsible for deleting it (tests use try/finally).
    /// </summary>
    private static string WriteTempImage(string format, int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);

        var ext = format switch
        {
            "jpeg" => ".jpg",
            "png" => ".png",
            "webp" => ".webp",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var path = Path.Combine(Path.GetTempPath(), $"ppvalidation_{Guid.NewGuid():N}{ext}");

        using (var fs = File.Create(path))
        {
            switch (format)
            {
                case "jpeg":
                    image.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
                    break;
                case "png":
                    image.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                    break;
                case "webp":
                    image.Save(fs, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder());
                    break;
            }
        }

        return path;
    }

    [Fact]
    public async Task InstagramFeed_Jpeg_WithinLimits_IsValid()
    {
        var svc = CreateService();
        var path = WriteTempImage("jpeg", 1080, 1080); // 1:1, within 8MB and dims
        try
        {
            var size = new FileInfo(path).Length;
            var result = await svc.ValidateFileAsync(
                path, "image/jpeg", size, MediaType.Image, Platform.Instagram, Placement.Feed);

            Assert.Equal(ValidationStatus.Valid, result.Status);
            Assert.Empty(result.Errors);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InstagramFeed_Png_IsRejected()
    {
        var svc = CreateService();
        var path = WriteTempImage("png", 1080, 1080);
        try
        {
            var size = new FileInfo(path).Length;
            // Declared mime is png; the service also re-derives the real mime from bytes —
            // either way Instagram must reject it.
            var result = await svc.ValidateFileAsync(
                path, "image/png", size, MediaType.Image, Platform.Instagram, Placement.Feed);

            Assert.Equal(ValidationStatus.Invalid, result.Status);
            Assert.Contains(result.Errors, e => e.Code == MediaValidationErrorCodes.UnsupportedMimeType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InstagramFeed_Webp_IsRejected()
    {
        var svc = CreateService();
        var path = WriteTempImage("webp", 1080, 1080);
        try
        {
            var size = new FileInfo(path).Length;
            var result = await svc.ValidateFileAsync(
                path, "image/webp", size, MediaType.Image, Platform.Instagram, Placement.Feed);

            Assert.Equal(ValidationStatus.Invalid, result.Status);
            Assert.Contains(result.Errors, e => e.Code == MediaValidationErrorCodes.UnsupportedMimeType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FacebookFeed_Png_IsValid()
    {
        // The exact same PNG that Instagram rejects must remain valid for Facebook.
        var svc = CreateService();
        var path = WriteTempImage("png", 1200, 630); // recommended FB size, valid aspect
        try
        {
            var size = new FileInfo(path).Length;
            var result = await svc.ValidateFileAsync(
                path, "image/png", size, MediaType.Image, Platform.Facebook, Placement.Feed);

            Assert.NotEqual(ValidationStatus.Invalid, result.Status);
            Assert.DoesNotContain(result.Errors, e => e.Code == MediaValidationErrorCodes.UnsupportedMimeType);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InstagramFeed_OverWideJpeg_WarnsButIsNotError()
    {
        // 1500px wide exceeds IG max (1440) but the aspect ratio (1:1 → use square 1500x1500)
        // stays in range. Meta downscales, so this must WARN, not error.
        var svc = CreateService();
        var path = WriteTempImage("jpeg", 1500, 1500);
        try
        {
            var size = new FileInfo(path).Length;
            var result = await svc.ValidateFileAsync(
                path, "image/jpeg", size, MediaType.Image, Platform.Instagram, Placement.Feed);

            Assert.DoesNotContain(result.Errors, e => e.Code == MediaValidationErrorCodes.DimensionsTooLarge);
            Assert.Contains(result.Warnings,
                w => w.Code == MediaValidationWarningCodes.DimensionsAboveMaxWillDownscale);
            Assert.Equal(ValidationStatus.Warning, result.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FacebookFeed_OverLargeImage_IsHardError()
    {
        // Facebook max is NOT advisory: a 3000x3000 image (> 2048) must hard-fail dimensions.
        var svc = CreateService();
        var path = WriteTempImage("jpeg", 3000, 3000);
        try
        {
            var size = new FileInfo(path).Length;
            var result = await svc.ValidateFileAsync(
                path, "image/jpeg", size, MediaType.Image, Platform.Facebook, Placement.Feed);

            Assert.Equal(ValidationStatus.Invalid, result.Status);
            Assert.Contains(result.Errors, e => e.Code == MediaValidationErrorCodes.DimensionsTooLarge);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Video extractor stub — image tests must never call it.</summary>
    private sealed class ThrowingVideoExtractor : IVideoMetadataExtractor
    {
        public Task<VideoMetadata?> ExtractAsync(string filePath) =>
            throw new InvalidOperationException("Video extractor should not be called for image validation.");

        public Task<bool> IsAvailableAsync() => Task.FromResult(false);
    }
}
