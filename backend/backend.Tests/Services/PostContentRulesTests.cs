using PostPilot.Api.Enums;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests.Services;

/// <summary>
/// Unit matrix for the central Story no-text rule. Every enforcement point (create, update,
/// story publisher preflight) routes through <see cref="PostContentRules.GetStoryTextError"/>,
/// so this matrix is the single source of truth for what counts as "no text".
/// </summary>
public class PostContentRulesTests
{
    // ── Accepted "no text" values (null / missing / empty string) ───────────────

    [Theory]
    [InlineData(Platform.Facebook, null)]
    [InlineData(Platform.Facebook, "")]
    [InlineData(Platform.Instagram, null)]
    [InlineData(Platform.Instagram, "")]
    public void Story_NullOrEmptyContent_IsAccepted(Platform platform, string? content)
    {
        Assert.Null(PostContentRules.GetStoryTextError(platform, PostType.Story, content));
    }

    // ── Rejected values: ordinary text AND whitespace-only (never trimmed) ──────

    [Theory]
    [InlineData("hello")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("  \t \n  ")]
    public void FacebookStory_NonEmptyContent_IsRejected_WithFacebookMessage(string content)
    {
        var error = PostContentRules.GetStoryTextError(Platform.Facebook, PostType.Story, content);

        Assert.Equal("Facebook Story posts do not support post text.", error);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("  \t \n  ")]
    public void InstagramStory_NonEmptyContent_IsRejected_WithInstagramMessage(string content)
    {
        var error = PostContentRules.GetStoryTextError(Platform.Instagram, PostType.Story, content);

        Assert.Equal("Instagram Story posts do not support captions.", error);
    }

    [Fact]
    public void OtherPlatformStory_NonEmptyContent_IsRejected_WithGenericMessage()
    {
        // Stories are only creatable for FB/IG (separate rule), but the text rule must not
        // silently pass hidden text through for any story row it cannot attribute.
        var error = PostContentRules.GetStoryTextError(Platform.Twitter, PostType.Story, "x");

        Assert.Equal("Story posts do not support post text.", error);
    }

    // ── Feed placement is never restricted by this rule ─────────────────────────

    [Theory]
    [InlineData(Platform.Facebook, "hello")]
    [InlineData(Platform.Facebook, " ")]
    [InlineData(Platform.Facebook, null)]
    [InlineData(Platform.Instagram, "a caption #tag")]
    [InlineData(Platform.Instagram, "\n")]
    [InlineData(Platform.Instagram, null)]
    public void FeedPosts_AreNeverRestricted(Platform platform, string? content)
    {
        Assert.Null(PostContentRules.GetStoryTextError(platform, PostType.Feed, content));
    }
}

/// <summary>
/// Unit matrix for the central feed text-length rule. Every enforcement point (create,
/// update, and the feed publishers' pre-Meta preflight) routes through
/// <see cref="PostContentRules.GetTextTooLongError"/>, so this matrix is the single source
/// of truth for the placement-specific limits (Facebook Feed 5000, Instagram Feed 2200)
/// and for the counting convention (UTF-16 code units, matching JavaScript's .length).
/// </summary>
public class PostContentRulesTextLengthTests
{
    // ── Exactly the limit is accepted; one over is rejected ─────────────────────

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.Instagram, 2200)]
    [InlineData(Platform.LinkedIn, 3000)]
    [InlineData(Platform.Twitter, 280)]
    public void TextAtExactLimit_IsAccepted(Platform platform, int limit)
    {
        Assert.Null(PostContentRules.GetTextTooLongError(platform, new string('x', limit)));
    }

    [Theory]
    [InlineData(Platform.Facebook, 5000)]
    [InlineData(Platform.Instagram, 2200)]
    [InlineData(Platform.LinkedIn, 3000)]
    [InlineData(Platform.Twitter, 280)]
    public void TextOneOverLimit_IsRejected(Platform platform, int limit)
    {
        var error = PostContentRules.GetTextTooLongError(platform, new string('x', limit + 1));

        Assert.Equal($"Text is too long for {platform}. Max {limit} characters.", error);
    }

    [Theory]
    [InlineData(Platform.Facebook, null)]
    [InlineData(Platform.Facebook, "")]
    [InlineData(Platform.Facebook, "an ordinary post")]
    [InlineData(Platform.Instagram, null)]
    [InlineData(Platform.Instagram, "")]
    [InlineData(Platform.Instagram, "an ordinary caption #tag")]
    public void OrdinaryOrEmptyText_IsAccepted(Platform platform, string? content)
    {
        Assert.Null(PostContentRules.GetTextTooLongError(platform, content));
    }

    // ── Placement isolation: the same text can fit Facebook but not Instagram ───

    [Fact]
    public void SameThreeThousandCharText_FitsFacebook_ButNotInstagram()
    {
        var content = new string('x', 3000);

        Assert.Null(PostContentRules.GetTextTooLongError(Platform.Facebook, content));
        Assert.Equal(
            "Text is too long for Instagram. Max 2200 characters.",
            PostContentRules.GetTextTooLongError(Platform.Instagram, content));
    }

    // ── Counting convention: UTF-16 code units, matching JS .length ─────────────

    [Fact]
    public void LineBreaksAndSpaces_CountTowardTheLimit()
    {
        // 2198 letters + "\n " = 2200 code units → exactly at the Instagram limit.
        var atLimit = new string('x', 2198) + "\n ";
        Assert.Equal(2200, atLimit.Length);
        Assert.Null(PostContentRules.GetTextTooLongError(Platform.Instagram, atLimit));

        // One more space → 2201 → rejected.
        Assert.NotNull(PostContentRules.GetTextTooLongError(Platform.Instagram, atLimit + " "));
    }

    [Fact]
    public void Emoji_CountsAsTwoUtf16CodeUnits_SameAsJavaScriptLength()
    {
        // "😀" is one surrogate pair = 2 UTF-16 code units in BOTH .NET string.Length and
        // JavaScript .length, so the frontend counter and this rule agree on the boundary.
        const string emoji = "\U0001F600"; // 😀
        Assert.Equal(2, emoji.Length);

        // Instagram: 2198 + 2 = 2200 → accepted; 2199 + 2 = 2201 → rejected.
        Assert.Null(PostContentRules.GetTextTooLongError(Platform.Instagram, new string('x', 2198) + emoji));
        Assert.NotNull(PostContentRules.GetTextTooLongError(Platform.Instagram, new string('x', 2199) + emoji));

        // Facebook: 4998 + 2 = 5000 → accepted; 4999 + 2 = 5001 → rejected.
        Assert.Null(PostContentRules.GetTextTooLongError(Platform.Facebook, new string('x', 4998) + emoji));
        Assert.NotNull(PostContentRules.GetTextTooLongError(Platform.Facebook, new string('x', 4999) + emoji));
    }

    [Fact]
    public void NonAsciiBmpText_CountsOneCodeUnitPerCharacter()
    {
        // Accented/Cyrillic BMP characters are 1 code unit each in both layers.
        var atLimit = new string('é', 2200);
        Assert.Null(PostContentRules.GetTextTooLongError(Platform.Instagram, atLimit));
        Assert.NotNull(PostContentRules.GetTextTooLongError(Platform.Instagram, atLimit + "й"));
    }
}
