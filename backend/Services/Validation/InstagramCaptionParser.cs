using System.Text.RegularExpressions;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Shared, authoritative parser that counts the entity OCCURRENCES inside an Instagram
/// caption — hashtags and @mentions — for the caption caps enforced by
/// <see cref="PostContentRules.GetInstagramFeedTagErrors"/>. This is the single backend
/// counting implementation; controllers, publishers and tests all route through it rather
/// than re-declaring a regex. The frontend mirrors these exact patterns in
/// <c>frontend/src/utils/instagramCaption.ts</c> so the composer never accepts a caption the
/// backend would reject.
///
/// <para>Counting rules (matching the frontend):</para>
/// <list type="bullet">
///   <item>Every occurrence counts, including duplicates: <c>#a #a</c> is 2 hashtags,
///   <c>@a @a</c> is 2 mentions.</item>
///   <item>A lone <c>#</c> or <c>@</c> with no following entity text does not count.</item>
///   <item>Hashtags are Unicode-aware (letters/numbers/underscore in any language), matching
///   the app's established hashtag parser; a hashtag must start at a non-word boundary so
///   <c>word#tag</c> is not counted.</item>
///   <item>Mentions use the Instagram username alphabet (ASCII letters/digits/dot/underscore)
///   and must start at a boundary that is not a word character or dot, so an email address
///   such as <c>person@example.com</c> is not counted as a mention.</item>
/// </list>
/// </summary>
public static class InstagramCaptionParser
{
    // Unicode-aware hashtag: '#' at a word boundary followed by 1+ letters/numbers/underscore.
    private static readonly Regex HashtagRegex = new(
        @"(?<![\p{L}\p{N}_])#[\p{L}\p{N}_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ASCII mention: '@' not preceded by a word char or dot (skips emails), 1–30 username chars.
    private static readonly Regex MentionRegex = new(
        @"(?<![A-Za-z0-9_.])@[A-Za-z0-9._]{1,30}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Counts hashtag occurrences in <paramref name="caption"/> (duplicates included).</summary>
    public static int CountHashtags(string? caption)
        => string.IsNullOrEmpty(caption) ? 0 : HashtagRegex.Matches(caption).Count;

    /// <summary>Counts @mention occurrences in <paramref name="caption"/> (duplicates included).</summary>
    public static int CountMentions(string? caption)
        => string.IsNullOrEmpty(caption) ? 0 : MentionRegex.Matches(caption).Count;
}
