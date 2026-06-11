using PostPilot.Api.Services.Publishing;
using Xunit;

namespace PostPilot.Api.Tests.Services;

/// <summary>
/// Log-hygiene parity for the STORY publishers. Both
/// <see cref="FacebookStoryPublisher"/> and <see cref="InstagramStoryPublisher"/>
/// log a media reference around <c>GetPublishingUrlAsync</c>; like the feed
/// publishers, they must never leak a full storage key, a signed token, or the
/// high-entropy ids (userId / workspaceId / mediaId) that make the unauthenticated
/// fetch URL unguessable. These pin the shared redaction helpers.
/// </summary>
public class StoryPublisherLogHygieneTests
{
    // ── FacebookStoryPublisher ────────────────────────────────────────────────

    [Fact]
    public void Fb_RedactUrl_dropsSignedTokenInQueryString()
    {
        var signed = "https://abc.supabase.co/storage/v1/object/sign/postpilot-media/" +
                     "users/x/media/y/story.mp4?token=eyJhbGciOiJIUzI1NiJ9.SECRETSIG";

        var redacted = FacebookStoryPublisher.RedactUrl(signed);

        Assert.StartsWith("https://abc.supabase.co/...", redacted);
        Assert.DoesNotContain("token=", redacted);
        Assert.DoesNotContain("SECRETSIG", redacted);
        Assert.DoesNotContain("postpilot-media", redacted);
    }

    [Fact]
    public void Fb_RedactUrl_keepsHostAndPathTail()
    {
        var redacted = FacebookStoryPublisher.RedactUrl("https://cdn.example.com/path/to/my-photo.png?sig=abcdef123456");
        Assert.StartsWith("https://cdn.example.com/...", redacted);
        Assert.Contains("my-photo.png", redacted);
        Assert.DoesNotContain("sig=", redacted);
    }

    [Fact]
    public void Fb_RedactKey_hidesWorkspaceIdAndMediaId()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        var key = $"users/{userId:D}/workspaces/{workspaceId:D}/providers/meta-facebook/media/{mediaId:D}/story.jpg";

        var redacted = FacebookStoryPublisher.RedactKey(key);

        Assert.StartsWith("users/...", redacted);
        Assert.DoesNotContain(userId.ToString(), redacted);
        Assert.DoesNotContain(workspaceId.ToString(), redacted);
        Assert.DoesNotContain(mediaId.ToString(), redacted);
    }

    [Fact]
    public void Fb_RedactKey_redactsLegacyShapeAndExternalUrl()
    {
        Assert.StartsWith("media/...", FacebookStoryPublisher.RedactKey($"media/{Guid.NewGuid():N}.jpg"));
        Assert.StartsWith("https://cdn.example.com/...",
            FacebookStoryPublisher.RedactKey("https://cdn.example.com/a/b/asset.jpg?sig=zzz"));
    }

    [Fact]
    public void Fb_Redact_handlesNullAndEmpty()
    {
        Assert.Equal("(empty)", FacebookStoryPublisher.RedactUrl(null));
        Assert.Equal("(empty)", FacebookStoryPublisher.RedactUrl(""));
        Assert.Equal("(empty)", FacebookStoryPublisher.RedactKey(null));
        Assert.Equal("(empty)", FacebookStoryPublisher.RedactKey(""));
    }

    // ── InstagramStoryPublisher ───────────────────────────────────────────────

    [Fact]
    public void Ig_RedactUrl_dropsSignedTokenInQueryString()
    {
        var signed = "https://abc.supabase.co/storage/v1/object/sign/postpilot-media/" +
                     "users/x/media/y/story.mp4?token=eyJhbGciOiJIUzI1NiJ9.SECRETSIG";

        var redacted = InstagramStoryPublisher.RedactUrl(signed);

        Assert.StartsWith("https://abc.supabase.co/...", redacted);
        Assert.DoesNotContain("token=", redacted);
        Assert.DoesNotContain("SECRETSIG", redacted);
    }

    [Fact]
    public void Ig_RedactKey_hidesWorkspaceIdAndMediaId()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        var key = $"users/{userId:D}/workspaces/{workspaceId:D}/providers/meta-instagram/media/{mediaId:D}/story.jpg";

        var redacted = InstagramStoryPublisher.RedactKey(key);

        Assert.StartsWith("users/...", redacted);
        Assert.DoesNotContain(userId.ToString(), redacted);
        Assert.DoesNotContain(workspaceId.ToString(), redacted);
        Assert.DoesNotContain(mediaId.ToString(), redacted);
    }

    [Fact]
    public void Ig_Redact_handlesNullAndEmpty()
    {
        Assert.Equal("(empty)", InstagramStoryPublisher.RedactUrl(null));
        Assert.Equal("(empty)", InstagramStoryPublisher.RedactKey(null));
        Assert.Equal("(empty)", InstagramStoryPublisher.RedactKey(""));
    }
}
