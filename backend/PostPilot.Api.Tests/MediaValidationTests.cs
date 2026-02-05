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
        var rules = MediaValidationRules.GetRules(Platform.Facebook, Placement.Story, MediaType.Image);

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
        // Story placement not defined for Facebook
        Assert.False(MediaValidationRules.HasRules(Platform.Facebook, Placement.Story, MediaType.Image));
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
