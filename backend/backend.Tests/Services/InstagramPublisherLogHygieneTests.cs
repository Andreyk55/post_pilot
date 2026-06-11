using PostPilot.Api.Services.Publishing;
using Xunit;

namespace PostPilot.Api.Tests.Services;

/// <summary>
/// Pins the log-hygiene guarantees for <see cref="InstagramPublisher"/>'s redaction
/// helpers. IG publishing logs outbound media URLs (image_url / video_url) and the
/// storage key around <c>GetPublishingUrlAsync</c>; none of these may leak a signed
/// token, a full storage key, or the high-entropy ids (userId / workspaceId / mediaId)
/// that make the unauthenticated fetch URL unguessable.
///
/// These mirror the FacebookPagePublisher redaction tests so both publishers are held
/// to the same bar.
/// </summary>
public class InstagramPublisherLogHygieneTests
{
    [Fact]
    public void RedactUrl_dropsSignedTokenInQueryString()
    {
        // A realistic Supabase signed URL with the token at the very end.
        var signed = "https://abc.supabase.co/storage/v1/object/sign/postpilot-media/" +
                     "users/x/media/y/reel.mp4?token=eyJhbGciOiJIUzI1NiJ9.SECRETSIG";

        var redacted = InstagramPublisher.RedactUrl(signed);

        // Scheme + host survive for traceability...
        Assert.StartsWith("https://abc.supabase.co/...", redacted);
        // ...but the signed token must NOT, even though it's the tail of the raw string.
        Assert.DoesNotContain("token=", redacted);
        Assert.DoesNotContain("SECRETSIG", redacted);
        Assert.DoesNotContain("postpilot-media", redacted);
    }

    [Fact]
    public void RedactUrl_keepsHostAndPathTailForDebugging()
    {
        var url = "https://cdn.example.com/path/to/my-photo.png?sig=abcdef123456";

        var redacted = InstagramPublisher.RedactUrl(url);

        // Enough to debug: host + the filename tail of the PATH.
        Assert.StartsWith("https://cdn.example.com/...", redacted);
        Assert.Contains("my-photo.png", redacted);
        // The query/signature is gone.
        Assert.DoesNotContain("sig=", redacted);
    }

    [Fact]
    public void RedactUrl_handlesNullAndEmpty()
    {
        Assert.Equal("(empty)", InstagramPublisher.RedactUrl(null));
        Assert.Equal("(empty)", InstagramPublisher.RedactUrl(""));
    }

    [Fact]
    public void RedactKey_hidesWorkspaceIdAndMediaId()
    {
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        var key = $"users/{userId:D}/workspaces/{workspaceId:D}/providers/meta-instagram/media/{mediaId:D}/reel.mp4";

        var redacted = InstagramPublisher.RedactKey(key);

        // Leading scope segment is kept for debugging...
        Assert.StartsWith("users/...", redacted);
        // ...but none of the high-entropy ids may appear in full.
        Assert.DoesNotContain(userId.ToString(), redacted);
        Assert.DoesNotContain(workspaceId.ToString(), redacted);
        Assert.DoesNotContain(mediaId.ToString(), redacted);
    }

    [Fact]
    public void RedactKey_redactsLegacyKeyShape()
    {
        var key = $"media/{Guid.NewGuid():N}.jpg";

        var redacted = InstagramPublisher.RedactKey(key);

        Assert.StartsWith("media/...", redacted);
        Assert.NotEqual(key, redacted);
    }

    [Fact]
    public void RedactKey_treatsExternalUrlAsUrl()
    {
        var external = "https://cdn.example.com/path/to/asset.jpg?sig=abcdef123456";

        var redacted = InstagramPublisher.RedactKey(external);

        Assert.StartsWith("https://cdn.example.com/...", redacted);
        Assert.DoesNotContain("sig=", redacted);
    }

    [Fact]
    public void RedactKey_handlesNullAndEmpty()
    {
        Assert.Equal("(empty)", InstagramPublisher.RedactKey(null));
        Assert.Equal("(empty)", InstagramPublisher.RedactKey(""));
    }
}
