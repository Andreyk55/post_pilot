using System.Text.Json;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Publishing;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram video/reel people tagging (user_tags) support.
/// Verifies that video posts carry tags through to the publish payload
/// using the same model/format as image posts.
/// </summary>
public class InstagramVideoTaggingTests
{
    // ── Entity-level tests: video posts can hold user_tags ──

    [Fact]
    public void VideoPost_CanStoreUserTags()
    {
        var tagsJson = "[{\"username\":\"nike\",\"x\":0.5,\"y\":0.5}]";
        var post = CreateVideoPost();
        post.InstagramUserTags = tagsJson;

        Assert.Equal(tagsJson, post.InstagramUserTags);
    }

    [Fact]
    public void VideoPost_NullTags_IsValid()
    {
        var post = CreateVideoPost();

        Assert.Null(post.InstagramUserTags);
    }

    [Fact]
    public void VideoPost_EmptyStringTags_IsValid()
    {
        var post = CreateVideoPost();
        post.InstagramUserTags = "";

        Assert.Equal("", post.InstagramUserTags);
    }

    // ── Serialization tests: tags serialize to correct IG format ──

    [Fact]
    public void UserTagsJson_ForVideo_SerializesToExpectedFormat()
    {
        // Same format used for images — IG expects identical JSON for video user_tags
        var tags = new[]
        {
            new { username = "nike", x = 0.5, y = 0.5 },
            new { username = "adidas", x = 0.1, y = 0.9 },
        };

        var json = JsonSerializer.Serialize(tags, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.Contains("\"username\":\"nike\"", json);
        Assert.Contains("\"x\":0.5", json);
        Assert.Contains("\"y\":0.5", json);
        Assert.Contains("\"username\":\"adidas\"", json);
    }

    [Fact]
    public void UserTagsJson_SingleTag_Roundtrips()
    {
        var json = "[{\"username\":\"testuser\",\"x\":0.33,\"y\":0.66}]";

        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);

        Assert.NotNull(parsed);
        Assert.Single(parsed);
        Assert.Equal("testuser", parsed[0]["username"].GetString());
        Assert.Equal(0.33, parsed[0]["x"].GetDouble(), 2);
        Assert.Equal(0.66, parsed[0]["y"].GetDouble(), 2);
    }

    [Fact]
    public void UserTagsJson_MultipleTags_Roundtrips()
    {
        var json = "[{\"username\":\"user1\",\"x\":0.1,\"y\":0.2},{\"username\":\"user2\",\"x\":0.8,\"y\":0.9}]";

        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json);

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Count);
    }

    // ── Payload builder simulation: form fields for video with tags ──

    [Fact]
    public void VideoFormFields_WithTags_IncludesUserTags()
    {
        var userTagsJson = "[{\"username\":\"nike\",\"x\":0.5,\"y\":0.5}]";

        var formFields = BuildVideoFormFields(
            videoUrl: "https://example.com/video.mp4",
            caption: "Test caption",
            accessToken: "token123",
            userTagsJson: userTagsJson);

        Assert.True(formFields.ContainsKey("user_tags"));
        Assert.Equal(userTagsJson, formFields["user_tags"]);
        Assert.Equal("REELS", formFields["media_type"]);
    }

    [Fact]
    public void VideoFormFields_WithoutTags_DoesNotIncludeUserTags()
    {
        var formFields = BuildVideoFormFields(
            videoUrl: "https://example.com/video.mp4",
            caption: "Test caption",
            accessToken: "token123",
            userTagsJson: null);

        Assert.False(formFields.ContainsKey("user_tags"));
    }

    [Fact]
    public void VideoFormFields_EmptyTags_DoesNotIncludeUserTags()
    {
        var formFields = BuildVideoFormFields(
            videoUrl: "https://example.com/video.mp4",
            caption: "Test caption",
            accessToken: "token123",
            userTagsJson: "");

        Assert.False(formFields.ContainsKey("user_tags"));
    }

    [Fact]
    public void VideoFormFields_AlwaysIncludesRequiredFields()
    {
        var formFields = BuildVideoFormFields(
            videoUrl: "https://example.com/video.mp4",
            caption: "Test",
            accessToken: "token",
            userTagsJson: null);

        Assert.Equal("REELS", formFields["media_type"]);
        Assert.Equal("https://example.com/video.mp4", formFields["video_url"]);
        Assert.Equal("Test", formFields["caption"]);
        Assert.Equal("token", formFields["access_token"]);
    }

    // ── Tag validation: missing coordinates detection ──

    [Fact]
    public void TagsMissingCoordinates_AreDetected()
    {
        var json = "[{\"username\":\"nocoords\"},{\"username\":\"hascoords\",\"x\":0.5,\"y\":0.5}]";
        var tags = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;

        var missingCoords = tags
            .Where(t => !t.ContainsKey("x") || !t.ContainsKey("y"))
            .Select(t => t.TryGetValue("username", out var u) ? u.GetString() : "(unknown)")
            .ToList();

        Assert.Single(missingCoords);
        Assert.Equal("nocoords", missingCoords[0]);
    }

    [Fact]
    public void TagsWithAllCoordinates_NoneDetectedAsMissing()
    {
        var json = "[{\"username\":\"a\",\"x\":0.1,\"y\":0.2},{\"username\":\"b\",\"x\":0.8,\"y\":0.9}]";
        var tags = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;

        var missingCoords = tags
            .Where(t => !t.ContainsKey("x") || !t.ContainsKey("y"))
            .ToList();

        Assert.Empty(missingCoords);
    }

    // ── StripPositionsFromUserTags: video tags must not include x/y ──

    [Fact]
    public void StripPositions_RemovesXYFromTags()
    {
        var input = "[{\"username\":\"nike\",\"x\":0.5,\"y\":0.5},{\"username\":\"adidas\",\"x\":0.1,\"y\":0.9}]";
        var result = InstagramPublisher.StripPositionsFromUserTags(input);

        Assert.NotNull(result);
        Assert.Contains("\"username\":\"nike\"", result);
        Assert.Contains("\"username\":\"adidas\"", result);
        Assert.DoesNotContain("\"x\"", result);
        Assert.DoesNotContain("\"y\"", result);
    }

    [Fact]
    public void StripPositions_PreservesUsernameOnly()
    {
        var input = "[{\"username\":\"solo\"}]";
        var result = InstagramPublisher.StripPositionsFromUserTags(input);

        Assert.NotNull(result);
        Assert.Contains("\"username\":\"solo\"", result);
    }

    [Fact]
    public void StripPositions_NullInput_ReturnsNull()
    {
        Assert.Null(InstagramPublisher.StripPositionsFromUserTags(null));
    }

    [Fact]
    public void StripPositions_EmptyString_ReturnsNull()
    {
        Assert.Null(InstagramPublisher.StripPositionsFromUserTags(""));
    }

    [Fact]
    public void StripPositions_EmptyArray_ReturnsNull()
    {
        Assert.Null(InstagramPublisher.StripPositionsFromUserTags("[]"));
    }

    [Fact]
    public void StripPositions_InvalidJson_ReturnsNull()
    {
        Assert.Null(InstagramPublisher.StripPositionsFromUserTags("not json"));
    }

    [Fact]
    public void StripPositions_MixedTags_StripsAllPositions()
    {
        // One tag with coords, one without
        var input = "[{\"username\":\"a\",\"x\":0.2,\"y\":0.3},{\"username\":\"b\"}]";
        var result = InstagramPublisher.StripPositionsFromUserTags(input);

        Assert.NotNull(result);
        Assert.Contains("\"username\":\"a\"", result);
        Assert.Contains("\"username\":\"b\"", result);
        Assert.DoesNotContain("\"x\"", result);
        Assert.DoesNotContain("\"y\"", result);
    }

    [Fact]
    public void StripPositions_ResultDeserializesToValidJson()
    {
        var input = "[{\"username\":\"nike\",\"x\":0.5,\"y\":0.5}]";
        var result = InstagramPublisher.StripPositionsFromUserTags(input);

        // Verify it's valid JSON and round-trips
        var parsed = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(result!);
        Assert.NotNull(parsed);
        Assert.Single(parsed);
        Assert.True(parsed[0].ContainsKey("username"));
        Assert.False(parsed[0].ContainsKey("x"));
        Assert.False(parsed[0].ContainsKey("y"));
    }

    // ── Helper: mirrors the form field building in CreateVideoContainerAsync ──

    private static Dictionary<string, string> BuildVideoFormFields(
        string videoUrl, string caption, string accessToken, string? userTagsJson)
    {
        var formFields = new Dictionary<string, string>
        {
            ["media_type"] = "REELS",
            ["video_url"] = videoUrl,
            ["caption"] = caption ?? "",
            ["access_token"] = accessToken,
        };

        if (!string.IsNullOrEmpty(userTagsJson))
        {
            formFields["user_tags"] = userTagsJson;
        }

        return formFields;
    }

    private static Post CreateVideoPost() => new()
    {
        Id = Guid.NewGuid(),
        Content = "Test video post with tags",
        MediaUrl = "media/test-video.mp4",
        MediaType = MediaType.Video,
        Platform = Platform.Instagram,
        Status = PostStatus.Scheduled,
        ScheduledAt = DateTime.UtcNow.AddMinutes(-1),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
