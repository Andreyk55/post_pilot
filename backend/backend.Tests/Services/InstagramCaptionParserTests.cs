using System.Linq;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests.Services;

/// <summary>
/// Unit matrix for the shared caption entity counter. This is the single backend counting
/// implementation behind the Instagram Feed hashtag/@mention caps, and it mirrors the
/// frontend's <c>utils/instagramCaption.ts</c> patterns exactly, so both layers count the same
/// caption text the same way. Occurrences are counted (duplicates included); media tags are a
/// separate feature and never appear in caption text.
/// </summary>
public class InstagramCaptionParserTests
{
    private static string Repeat(string token, int count) =>
        string.Join(" ", Enumerable.Repeat(token, count));

    // ── Hashtags ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("no hashtags here", 0)]
    [InlineData("#travel #summer", 2)]
    [InlineData("#travel #travel", 2)]          // duplicates count separately
    [InlineData("#a #b #c", 3)]
    [InlineData("#tag1 number-suffixed", 1)]
    public void CountHashtags_CountsOccurrences(string? caption, int expected)
    {
        Assert.Equal(expected, InstagramCaptionParser.CountHashtags(caption));
    }

    [Theory]
    [InlineData("#", 0)]                          // lone # is not a hashtag
    [InlineData("# ", 0)]
    [InlineData("a # b", 0)]
    [InlineData("word#tag", 0)]                   // must start at a word boundary
    public void CountHashtags_LoneOrMidWordHash_DoesNotCount(string caption, int expected)
    {
        Assert.Equal(expected, InstagramCaptionParser.CountHashtags(caption));
    }

    [Theory]
    [InlineData("#путешествие #лето", 2)]         // Cyrillic
    [InlineData("#日本 #写真", 2)]                  // CJK
    [InlineData("#café", 1)]                       // accented Latin
    public void CountHashtags_IsUnicodeAware(string caption, int expected)
    {
        Assert.Equal(expected, InstagramCaptionParser.CountHashtags(caption));
    }

    // ── Mentions ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("no mentions", 0)]
    [InlineData("@account1 @account2", 2)]
    [InlineData("@account @account", 2)]         // duplicates count separately
    [InlineData("Follow @john_doe.99 today", 1)]
    public void CountMentions_CountsOccurrences(string? caption, int expected)
    {
        Assert.Equal(expected, InstagramCaptionParser.CountMentions(caption));
    }

    [Theory]
    [InlineData("@", 0)]                          // lone @ is not a mention
    [InlineData("@ nope", 0)]
    [InlineData("email person@example.com now", 0)] // email is not a mention
    [InlineData("nested a@b handle", 0)]
    public void CountMentions_LoneAtOrEmail_DoesNotCount(string caption, int expected)
    {
        Assert.Equal(expected, InstagramCaptionParser.CountMentions(caption));
    }

    // ── Boundary counts used by the caps (30 hashtags / 20 mentions) ────────────

    [Fact]
    public void CountHashtags_ExactlyThirtyAndThirtyOne()
    {
        Assert.Equal(30, InstagramCaptionParser.CountHashtags(Repeat("#tag", 30)));
        Assert.Equal(31, InstagramCaptionParser.CountHashtags(Repeat("#tag", 31)));
    }

    [Fact]
    public void CountMentions_ExactlyTwentyAndTwentyOne()
    {
        Assert.Equal(20, InstagramCaptionParser.CountMentions(Repeat("@user", 20)));
        Assert.Equal(21, InstagramCaptionParser.CountMentions(Repeat("@user", 21)));
    }

    // ── Idempotent / non-mutating: counting the same caption twice is stable ────

    [Fact]
    public void Counting_IsRepeatable_AndCombinedContentIsCountedIndependently()
    {
        const string caption = "Trip! @natgeo @nasa #travel #travel #space";
        Assert.Equal(2, InstagramCaptionParser.CountMentions(caption));
        Assert.Equal(3, InstagramCaptionParser.CountHashtags(caption));
        // Re-count: no regex lastIndex state leaks between calls.
        Assert.Equal(2, InstagramCaptionParser.CountMentions(caption));
        Assert.Equal(3, InstagramCaptionParser.CountHashtags(caption));
    }
}
