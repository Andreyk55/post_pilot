using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;

namespace PostPilot.Api;

/// <summary>
/// Centralized validation limits for the application.
/// </summary>
public static class ValidationLimits
{
    // Voice Profile limits
    public const int VoiceProfileNameMinLength = 1;
    public const int VoiceProfileNameMaxLength = 60;
    public const int VoiceProfileDescriptionMaxLength = 300;
    public const int VoiceProfileDoRulesMaxLength = 1500;
    public const int VoiceProfileDontRulesMaxLength = 1500;
    public const int VoiceProfileBannedWordsMaxLength = 800;
    public const int VoiceProfileExamplePostsMaxLength = 4000;
    public const int VoiceProfileTotalMaxLength = 8000;

    // Post limits (general fallback)
    public const int PostTextMaxLength = 5000;
    public const int PostTitleMaxLength = 120; // if exists
    public const int PostMaxHashtags = 50; // if stored separately
    public const int PostMaxMediaFiles = 10;

    // Platform-specific post text limits
    public const int PostTextMaxLengthFacebook = 5000;
    public const int PostTextMaxLengthInstagram = 2200;
    public const int PostTextMaxLengthLinkedIn = 3000;
    public const int PostTextMaxLengthX = 280;

    /// <summary>
    /// Platform-specific post text limits (for Post entities).
    /// </summary>
    public static readonly IReadOnlyDictionary<Platform, int> PostTextMaxCharsByPlatform =
        new Dictionary<Platform, int>
        {
            { Platform.Facebook, PostTextMaxLengthFacebook },
            { Platform.Instagram, PostTextMaxLengthInstagram },
            { Platform.LinkedIn, PostTextMaxLengthLinkedIn },
            { Platform.Twitter, PostTextMaxLengthX },
        };

    /// <summary>
    /// Platform-specific post text limits (for AI generation).
    /// </summary>
    public static readonly IReadOnlyDictionary<AiPlatform, int> PostTextMaxCharsByAiPlatform =
        new Dictionary<AiPlatform, int>
        {
            { AiPlatform.Facebook, PostTextMaxLengthFacebook },
            { AiPlatform.Instagram, PostTextMaxLengthInstagram },
            { AiPlatform.LinkedIn, PostTextMaxLengthLinkedIn },
            { AiPlatform.X, PostTextMaxLengthX },
        };

    /// <summary>
    /// Gets the maximum post text length for a given platform.
    /// </summary>
    public static int GetPostTextMaxChars(Platform platform)
    {
        return PostTextMaxCharsByPlatform.TryGetValue(platform, out var limit)
            ? limit
            : PostTextMaxLength;
    }

    /// <summary>
    /// Gets the maximum post text length for a given AI platform.
    /// </summary>
    public static int GetPostTextMaxChars(AiPlatform platform)
    {
        return PostTextMaxCharsByAiPlatform.TryGetValue(platform, out var limit)
            ? limit
            : PostTextMaxLength;
    }

    // Media limits (in bytes)
    public const long MediaImageMaxBytes = 20L * 1024 * 1024; // 20MB
    public const long MediaVideoMaxBytes = 200L * 1024 * 1024; // 200MB
}