using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Authoritative platform/placement rules for POST TEXT (Post.Content), the text counterpart
/// of <see cref="MediaValidationRules"/>. Every enforcement path — create, update (scheduling
/// and publish-now both go through create/update), and the story publishers' pre-Meta
/// preflight — must route through this class rather than re-implementing the rule locally.
/// </summary>
public static class PostContentRules
{
    /// <summary>
    /// Returns the blocking error message when <paramref name="content"/> carries text that a
    /// Story cannot have, or null when the combination is acceptable.
    ///
    /// <para>Stories have no text/caption field on either Facebook or Instagram, and the UI
    /// renders none — so ANY non-empty content on a Story is a hidden-text attempt from a
    /// crafted or outdated client and must be rejected, never silently dropped.</para>
    ///
    /// <para>Accepted "no text" values: null, missing field, and empty string (the entity
    /// convention — <see cref="Entities.Post.Content"/> defaults to <c>string.Empty</c>).
    /// Whitespace-only content (" ", "\n", "\t", …) counts as text: post content is stored
    /// verbatim (never trimmed/normalized before validation), so a whitespace payload is
    /// still hidden content and is rejected rather than trimmed and accepted.</para>
    ///
    /// <para>Feed placement is never restricted here — feed text rules live in
    /// <see cref="GetTextTooLongError"/>. Callers must pass the RESOLVED platform and post
    /// type (from the request on create, from the stored row on update/publish), never an
    /// assumption.</para>
    /// </summary>
    public static string? GetStoryTextError(Platform platform, PostType postType, string? content)
    {
        if (postType != PostType.Story)
            return null;

        if (string.IsNullOrEmpty(content))
            return null;

        return platform switch
        {
            Platform.Instagram => "Instagram Story posts do not support captions.",
            Platform.Facebook => "Facebook Story posts do not support post text.",
            _ => "Story posts do not support post text.",
        };
    }

    /// <summary>
    /// Returns the blocking error message when <paramref name="content"/> exceeds the
    /// platform's post-text limit (<see cref="ValidationLimits.GetPostTextMaxChars(Platform)"/>:
    /// Facebook Feed 5000, Instagram Feed 2200), or null when it fits. Exactly the limit is
    /// accepted; one over is rejected.
    ///
    /// <para>This is the FEED text rule — placement-specific in effect because Stories carry
    /// no text at all (<see cref="GetStoryTextError"/> rejects any non-empty Story content
    /// before a length check could matter). Length is counted in UTF-16 code units
    /// (<c>string.Length</c>), matching the frontend's JavaScript <c>.length</c> so both
    /// layers agree on the boundary for emoji/non-ASCII text.</para>
    /// </summary>
    public static string? GetTextTooLongError(Platform platform, string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        var maxChars = ValidationLimits.GetPostTextMaxChars(platform);
        return content.Length > maxChars
            ? $"Text is too long for {platform}. Max {maxChars} characters."
            : null;
    }
}
