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

    // Post limits
    public const int PostTextMaxLength = 5000;
    public const int PostTitleMaxLength = 120; // if exists
    public const int PostMaxHashtags = 50; // if stored separately
    public const int PostMaxMediaFiles = 10;

    // Media limits (in bytes)
    public const long MediaImageMaxBytes = 20L * 1024 * 1024; // 20MB
    public const long MediaVideoMaxBytes = 200L * 1024 * 1024; // 200MB
}