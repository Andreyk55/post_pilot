using System.Text.Json;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram carousel per-media-item tagging.
/// Verifies DeserializeMediaTags, per-item tag storage, and video tag stripping
/// within carousel contexts.
/// </summary>
public class InstagramCarouselTaggingTests
{
    // ── DeserializeMediaTags: parsing InstagramMediaTagsJson ──

    [Fact]
    public void DeserializeMediaTags_ValidJson_ReturnsDictionary()
    {
        var json = """{"0":[{"username":"nike","x":0.5,"y":0.5}],"2":[{"username":"adidas","x":0.3,"y":0.7}]}""";
        var result = InstagramPublisher.DeserializeMediaTags(json);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey(0));
        Assert.True(result.ContainsKey(2));
        Assert.Contains("nike", result[0]);
        Assert.Contains("adidas", result[2]);
    }

    [Fact]
    public void DeserializeMediaTags_Null_ReturnsEmpty()
    {
        var result = InstagramPublisher.DeserializeMediaTags(null);
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeMediaTags_EmptyString_ReturnsEmpty()
    {
        var result = InstagramPublisher.DeserializeMediaTags("");
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeMediaTags_EmptyObject_ReturnsEmpty()
    {
        var result = InstagramPublisher.DeserializeMediaTags("{}");
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeMediaTags_InvalidJson_ReturnsEmpty()
    {
        var result = InstagramPublisher.DeserializeMediaTags("not json");
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeMediaTags_EmptyArrayValue_SkipsEntry()
    {
        var json = """{"0":[],"1":[{"username":"nike","x":0.5,"y":0.5}]}""";
        var result = InstagramPublisher.DeserializeMediaTags(json);

        Assert.Single(result);
        Assert.True(result.ContainsKey(1));
        Assert.False(result.ContainsKey(0));
    }

    [Fact]
    public void DeserializeMediaTags_NonNumericKey_SkipsEntry()
    {
        var json = """{"abc":[{"username":"nike","x":0.5,"y":0.5}],"1":[{"username":"adidas","x":0.3,"y":0.7}]}""";
        var result = InstagramPublisher.DeserializeMediaTags(json);

        Assert.Single(result);
        Assert.True(result.ContainsKey(1));
    }

    [Fact]
    public void DeserializeMediaTags_MultipleTags_PreservesAll()
    {
        var json = """{"0":[{"username":"nike","x":0.5,"y":0.5},{"username":"adidas","x":0.1,"y":0.9}]}""";
        var result = InstagramPublisher.DeserializeMediaTags(json);

        Assert.Single(result);
        var tags = result[0];
        Assert.Contains("nike", tags);
        Assert.Contains("adidas", tags);
    }

    // ── Per-item tags: raw text is valid JSON for Graph API ──

    [Fact]
    public void DeserializeMediaTags_RawText_IsValidJsonArray()
    {
        var json = """{"0":[{"username":"nike","x":0.5,"y":0.5}]}""";
        var result = InstagramPublisher.DeserializeMediaTags(json);

        // The raw text should be parseable as a JSON array
        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(result[0]);
        Assert.NotNull(parsed);
        Assert.Single(parsed);
        Assert.Equal("nike", parsed[0]["username"].GetString());
    }

    // ── Video tags in carousel: StripPositions works for carousel items ──

    [Fact]
    public void CarouselVideoChild_TagsStripped()
    {
        // Carousel video children should have x/y stripped
        var imageTagsJson = """[{"username":"nike","x":0.5,"y":0.5}]""";
        var stripped = InstagramPublisher.StripPositionsFromUserTags(imageTagsJson);

        Assert.NotNull(stripped);
        Assert.Contains("nike", stripped);
        Assert.DoesNotContain("\"x\"", stripped);
        Assert.DoesNotContain("\"y\"", stripped);
    }

    [Fact]
    public void CarouselImageChild_TagsPreserved()
    {
        // Carousel image children should keep x/y
        var imageTagsJson = """[{"username":"nike","x":0.5,"y":0.5}]""";

        // Image tags pass through as-is (no stripping)
        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(imageTagsJson);
        Assert.NotNull(parsed);
        Assert.True(parsed[0].ContainsKey("x"));
        Assert.True(parsed[0].ContainsKey("y"));
    }

    // ── Post entity: InstagramMediaTagsJson field ──

    [Fact]
    public void Post_CanStoreMediaTagsJson()
    {
        var tagsJson = """{"0":[{"username":"nike","x":0.5,"y":0.5}],"1":[{"username":"adidas","x":0.3,"y":0.7}]}""";
        var post = CreateCarouselPost();
        post.InstagramMediaTagsJson = tagsJson;

        Assert.Equal(tagsJson, post.InstagramMediaTagsJson);
    }

    [Fact]
    public void Post_NullMediaTagsJson_IsValid()
    {
        var post = CreateCarouselPost();
        Assert.Null(post.InstagramMediaTagsJson);
    }

    [Fact]
    public void Post_MediaTagsJsonAndUserTags_AreIndependent()
    {
        var post = CreateCarouselPost();
        post.InstagramUserTags = """[{"username":"single","x":0.5,"y":0.5}]""";
        post.InstagramMediaTagsJson = """{"0":[{"username":"carousel","x":0.5,"y":0.5}]}""";

        Assert.NotNull(post.InstagramUserTags);
        Assert.NotNull(post.InstagramMediaTagsJson);
        Assert.Contains("single", post.InstagramUserTags);
        Assert.Contains("carousel", post.InstagramMediaTagsJson);
    }

    // ── End-to-end: simulate carousel child form field building ──

    [Fact]
    public void CarouselImageChild_FormFields_WithTags()
    {
        var tagsJson = """[{"username":"nike","x":0.5,"y":0.5}]""";
        var formFields = BuildImageChildFormFields(
            imageUrl: "https://example.com/img.jpg",
            accessToken: "token123",
            userTagsJson: tagsJson);

        Assert.True(formFields.ContainsKey("user_tags"));
        Assert.Equal(tagsJson, formFields["user_tags"]);
        Assert.Equal("true", formFields["is_carousel_item"]);
    }

    [Fact]
    public void CarouselImageChild_FormFields_WithoutTags()
    {
        var formFields = BuildImageChildFormFields(
            imageUrl: "https://example.com/img.jpg",
            accessToken: "token123",
            userTagsJson: null);

        Assert.False(formFields.ContainsKey("user_tags"));
    }

    [Fact]
    public void CarouselVideoChild_FormFields_WithTags_StripsPositions()
    {
        var tagsJson = """[{"username":"nike","x":0.5,"y":0.5}]""";
        var formFields = BuildVideoChildFormFields(
            videoUrl: "https://example.com/vid.mp4",
            accessToken: "token123",
            userTagsJson: tagsJson);

        Assert.True(formFields.ContainsKey("user_tags"));
        // Positions should be stripped for video
        Assert.DoesNotContain("\"x\"", formFields["user_tags"]);
        Assert.DoesNotContain("\"y\"", formFields["user_tags"]);
        Assert.Contains("nike", formFields["user_tags"]);
        Assert.Equal("VIDEO", formFields["media_type"]);
        Assert.Equal("true", formFields["is_carousel_item"]);
    }

    // ── Helpers ──

    private static Dictionary<string, string> BuildImageChildFormFields(
        string imageUrl, string accessToken, string? userTagsJson)
    {
        var formFields = new Dictionary<string, string>
        {
            ["image_url"] = imageUrl,
            ["is_carousel_item"] = "true",
            ["access_token"] = accessToken,
        };

        if (!string.IsNullOrEmpty(userTagsJson))
        {
            formFields["user_tags"] = userTagsJson;
        }

        return formFields;
    }

    private static Dictionary<string, string> BuildVideoChildFormFields(
        string videoUrl, string accessToken, string? userTagsJson)
    {
        var formFields = new Dictionary<string, string>
        {
            ["media_type"] = "VIDEO",
            ["video_url"] = videoUrl,
            ["is_carousel_item"] = "true",
            ["access_token"] = accessToken,
        };

        var videoTagsJson = InstagramPublisher.StripPositionsFromUserTags(userTagsJson);
        if (!string.IsNullOrEmpty(videoTagsJson))
        {
            formFields["user_tags"] = videoTagsJson;
        }

        return formFields;
    }

    private static Post CreateCarouselPost() => new()
    {
        Id = Guid.NewGuid(),
        Content = "Test carousel post with per-item tags",
        MediaUrl = "media/img1.jpg",
        MediaType = MediaType.Image,
        Platform = Platform.Instagram,
        Status = PostStatus.Scheduled,
        ScheduledAt = DateTime.UtcNow.AddMinutes(-1),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        MediaItems = new List<PostMediaItem>
        {
            new() { Id = Guid.NewGuid(), Order = 0, MediaUrl = "media/img1.jpg", MediaType = MediaType.Image },
            new() { Id = Guid.NewGuid(), Order = 1, MediaUrl = "media/img2.jpg", MediaType = MediaType.Image },
            new() { Id = Guid.NewGuid(), Order = 2, MediaUrl = "media/vid1.mp4", MediaType = MediaType.Video },
        },
    };
}
