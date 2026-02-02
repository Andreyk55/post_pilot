namespace PostPilot.Api.DTOs;

/// <summary>
/// Available AI text actions.
/// </summary>
public enum AiTextAction
{
    Polish,
    RewriteTone,
    Shorten,
    Expand,
    Hashtags,
    PreFlight,
    GenerateVariants
}

/// <summary>
/// Supported tones for rewriting text.
/// </summary>
public enum AiTone
{
    Professional,
    Casual,
    Funny,
    Sales,
    Humorous,
    Urgent,
    Inspirational
}

/// <summary>
/// Content goal for AI text generation.
/// </summary>
public enum AiGoal
{
    Engage,
    Promote,
    Announce,
    Educate,
    Story
}

/// <summary>
/// Desired length for generated text.
/// </summary>
public enum AiLength
{
    Short,
    Medium,
    Long
}

/// <summary>
/// Supported social media platforms for AI text optimization.
/// </summary>
public enum AiPlatform
{
    Facebook,
    Instagram,
    LinkedIn,
    X
}

/// <summary>
/// Request for AI text assistance.
/// </summary>
public record AiTextRequest(
    AiTextAction Action,
    AiPlatform Platform,
    string Text,
    AiTone? Tone = null,
    string Language = "en",
    Guid? VoiceProfileId = null
);

/// <summary>
/// Request for generating AI text variants with full control options.
/// </summary>
public record AiGenerateVariantsRequest(
    AiPlatform Platform,
    string InputText,
    AiGoal Goal,
    AiTone Tone,
    AiLength Length,
    bool IncludeEmojis = false,
    bool IncludeHashtags = false,
    bool IncludeCta = false,
    bool IncludeQuestion = false,
    int NumVariants = 3,
    string Language = "en",
    int? RegenerateIndex = null,
    Guid? VoiceProfileId = null
);

/// <summary>
/// A single generated text variant with unique ID for regeneration.
/// </summary>
public record AiGeneratedVariant(
    string Id,
    string Text
);

/// <summary>
/// Response containing generated text variants.
/// </summary>
public record AiGenerateVariantsResponse(
    List<AiGeneratedVariant> Variants
);

/// <summary>
/// A single text variant suggestion.
/// </summary>
public record AiTextVariant(
    string Title,
    string Text
);

/// <summary>
/// Response containing text variants (for Polish, RewriteTone, Shorten, Expand actions).
/// </summary>
public record AiTextVariantsResponse(
    AiTextAction Action,
    List<AiTextVariant> Variants
);

/// <summary>
/// Response containing hashtag suggestions.
/// </summary>
public record AiHashtagsResponse(
    AiTextAction Action,
    List<string> Hashtags
);

/// <summary>
/// Severity level for pre-flight issues.
/// </summary>
public enum AiIssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// A single pre-flight check issue.
/// </summary>
public record AiPreFlightIssue(
    AiIssueSeverity Severity,
    string Message,
    string? SuggestedFix
);

/// <summary>
/// Response containing pre-flight check results.
/// </summary>
public record AiPreFlightResponse(
    AiTextAction Action,
    int Score,
    List<AiPreFlightIssue> Issues
);

// ===== Multilingual Caption Assistance =====

/// <summary>
/// Request for language detection.
/// </summary>
public record LanguageDetectRequest(
    string Text
);

/// <summary>
/// Response containing detected language information.
/// </summary>
public record LanguageDetectResponse(
    string LanguageCode,
    double Confidence,
    bool IsReliable
);

/// <summary>
/// Request for multilingual caption generation.
/// </summary>
public record CaptionGenerateRequest(
    string Text,
    AiPlatform Platform,
    string? OutputLanguage = null,
    int Variants = 1,
    bool KeepBrandVoice = true,
    bool StrictMeaning = true,
    Guid? VoiceProfileId = null,
    /// <summary>
    /// If provided, the backend will skip language detection and use this value.
    /// This allows the frontend to cache detection results and avoid duplicate API calls.
    /// </summary>
    string? SourceLanguage = null
);

/// <summary>
/// Response containing generated multilingual captions.
/// </summary>
public record CaptionGenerateResponse(
    string SourceLanguage,
    double SourceConfidence,
    bool SourceIsReliable,
    string OutputLanguage,
    List<string> Captions,
    List<string> Warnings
);

// ===== Post Time Suggestion =====

/// <summary>
/// Audience location mode for post time suggestions.
/// </summary>
public enum AudienceLocationMode
{
    /// <summary>Audience mainly in user's timezone</summary>
    MyLocation,
    /// <summary>Audience mainly in a specific country's timezone</summary>
    SpecificCountry,
    /// <summary>Audience is spread globally / not sure</summary>
    Worldwide
}

/// <summary>
/// Request for AI-powered post time suggestions.
/// </summary>
public record PostTimeSuggestionRequest(
    AiPlatform Platform,
    AiGoal Goal,
    string PostText,
    string Weekday,
    string Timezone,
    AudienceLocationMode AudienceLocation = AudienceLocationMode.MyLocation,
    string? Country = null
);

/// <summary>
/// A single time suggestion with confidence and reason.
/// </summary>
public record TimeSuggestion(
    string Time,
    string Label,
    int Confidence,
    string Reason
);

/// <summary>
/// Response containing AI-suggested posting times.
/// </summary>
public record PostTimeSuggestionResponse(
    TimeSuggestion Primary,
    List<TimeSuggestion> Alternatives
);
