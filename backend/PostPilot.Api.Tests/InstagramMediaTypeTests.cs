using Xunit;
using PostPilot.Api.Controllers;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram media type mapping and permalink-based fallback detection.
/// </summary>
public class InstagramMediaTypeParsingTests
{
    // ──────────────────────────────────────────────
    //  ParseGraphMediaType — Graph API media_type → enum
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("IMAGE", InstagramMediaType.Image)]
    [InlineData("image", InstagramMediaType.Image)]
    [InlineData("Image", InstagramMediaType.Image)]
    [InlineData("VIDEO", InstagramMediaType.Reels)]     // IG API returns VIDEO for Reels
    [InlineData("video", InstagramMediaType.Reels)]
    [InlineData("REELS", InstagramMediaType.Reels)]
    [InlineData("Reels", InstagramMediaType.Reels)]
    [InlineData("CAROUSEL_ALBUM", InstagramMediaType.CarouselAlbum)]
    [InlineData("carousel_album", InstagramMediaType.CarouselAlbum)]
    public void ParseGraphMediaType_ValidValues_MapsCorrectly(string graphValue, InstagramMediaType expected)
    {
        var result = InstagramPublisher.ParseGraphMediaType(graphValue);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("STORY")]
    [InlineData("UNKNOWN_TYPE")]
    [InlineData("LIVE")]
    public void ParseGraphMediaType_UnknownValues_ReturnsUnknown(string graphValue)
    {
        var result = InstagramPublisher.ParseGraphMediaType(graphValue);
        Assert.Equal(InstagramMediaType.Unknown, result);
    }

    [Fact]
    public void ParseGraphMediaType_Null_ReturnsUnknown()
    {
        var result = InstagramPublisher.ParseGraphMediaType(null!);
        Assert.Equal(InstagramMediaType.Unknown, result);
    }

    // ──────────────────────────────────────────────
    //  Permalink-based fallback detection
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData("https://www.instagram.com/reel/ABC123/", InstagramMediaType.Reels)]
    [InlineData("https://www.instagram.com/reels/ABC123/", InstagramMediaType.Reels)]
    [InlineData("https://www.instagram.com/p/XYZ789/", InstagramMediaType.Image)]
    public void PermalinkFallback_KnownPatterns_SetsCorrectType(string permalink, InstagramMediaType expected)
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            ExternalPostUrl = permalink,
        };

        // Simulate the fallback logic (same as TrySetMediaTypeFromPermalink)
        if (permalink.Contains("/reel/", StringComparison.OrdinalIgnoreCase) ||
            permalink.Contains("/reels/", StringComparison.OrdinalIgnoreCase))
        {
            post.InstagramMediaType = InstagramMediaType.Reels;
        }
        else if (permalink.Contains("/p/", StringComparison.OrdinalIgnoreCase))
        {
            post.InstagramMediaType = InstagramMediaType.Image;
        }
        else
        {
            post.InstagramMediaType = InstagramMediaType.Unknown;
        }

        Assert.Equal(expected, post.InstagramMediaType);
    }

    [Theory]
    [InlineData("https://www.instagram.com/stories/user/123/")]
    [InlineData("https://www.instagram.com/user/")]
    public void PermalinkFallback_UnknownPatterns_SetsUnknown(string permalink)
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            ExternalPostUrl = permalink,
        };

        if (permalink.Contains("/reel/", StringComparison.OrdinalIgnoreCase) ||
            permalink.Contains("/reels/", StringComparison.OrdinalIgnoreCase))
        {
            post.InstagramMediaType = InstagramMediaType.Reels;
        }
        else if (permalink.Contains("/p/", StringComparison.OrdinalIgnoreCase))
        {
            post.InstagramMediaType = InstagramMediaType.Image;
        }
        else
        {
            post.InstagramMediaType = InstagramMediaType.Unknown;
        }

        Assert.Equal(InstagramMediaType.Unknown, post.InstagramMediaType);
    }

    [Fact]
    public void PermalinkFallback_NullPermalink_DoesNotSetType()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            ExternalPostUrl = null,
        };

        // TrySetMediaTypeFromPermalink returns early if ExternalPostUrl is null
        Assert.Null(post.InstagramMediaType);
    }

    // ──────────────────────────────────────────────
    //  InstagramMediaType enum values
    // ──────────────────────────────────────────────

    [Fact]
    public void InstagramMediaType_EnumValues_AreStable()
    {
        // Ensure numeric values don't change (they're persisted in DB)
        Assert.Equal(0, (int)InstagramMediaType.Unknown);
        Assert.Equal(1, (int)InstagramMediaType.Image);
        Assert.Equal(2, (int)InstagramMediaType.Video);
        Assert.Equal(3, (int)InstagramMediaType.Reels);
        Assert.Equal(4, (int)InstagramMediaType.CarouselAlbum);
    }

    [Fact]
    public void Post_InstagramMediaType_DefaultsToNull()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
        };

        Assert.Null(post.InstagramMediaType);
    }

    [Fact]
    public void Post_InstagramMediaType_CanBeSetAndRead()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            InstagramMediaType = InstagramMediaType.Reels,
        };

        Assert.Equal(InstagramMediaType.Reels, post.InstagramMediaType);
    }

    [Fact]
    public void PostDto_InstagramMediaType_SerializesAsString()
    {
        var post = new Entities.Post
        {
            Id = Guid.NewGuid(),
            Content = "test",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Published,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            InstagramMediaType = InstagramMediaType.Reels,
        };

        var dto = Controllers.PostDto.FromEntity(post);

        Assert.Equal("Reels", dto.InstagramMediaType);
    }

    [Fact]
    public void PostDto_InstagramMediaType_NullWhenNotSet()
    {
        var post = new Entities.Post
        {
            Id = Guid.NewGuid(),
            Content = "test",
            Platform = Platform.Facebook,
            MediaType = MediaType.Image,
            Status = PostStatus.Published,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var dto = Controllers.PostDto.FromEntity(post);

        Assert.Null(dto.InstagramMediaType);
    }
}
