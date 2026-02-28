namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for AI service cache durations.
/// Bound from "Ai:CacheDurations" config section. All defaults in appsettings.common.json.
/// </summary>
public class AiCacheOptions
{
    public const string SectionName = "Ai:CacheDurations";

    /// <summary>
    /// Cache duration in minutes for caption assist responses.
    /// </summary>
    public int CaptionAssistMinutes { get; set; }

    /// <summary>
    /// Cache duration in minutes for language detection results.
    /// </summary>
    public int LanguageDetectionMinutes { get; set; }

    /// <summary>
    /// Cache duration in minutes for Google AI client (Gemini text) responses.
    /// </summary>
    public int GoogleAiClientMinutes { get; set; }

    /// <summary>
    /// Cache duration in minutes for AI post time suggestion responses.
    /// </summary>
    public int PostTimeSuggestionMinutes { get; set; }

    /// <summary>
    /// Download URL expiration in minutes for AI asset resolution.
    /// </summary>
    public int AssetResolverDownloadUrlExpirationMinutes { get; set; }
}
