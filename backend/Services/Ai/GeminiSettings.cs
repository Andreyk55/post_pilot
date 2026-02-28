namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Configuration for Gemini AI service.
/// Non-secret values (BaseUrl, TimeoutSeconds) bound from "Ai:Gemini" config section.
/// Secrets (ApiKey, Model, VisionModel) bound from "Gemini" config section, which is
/// populated via canonical env vars (Gemini__ApiKey, Gemini__Model, Gemini__VisionModel)
/// or legacy env vars (GEMINI_API_KEY, GEMINI_MODEL, GEMINI_VISION_MODEL) through
/// EnvVarMapper.
/// </summary>
public class GeminiSettings
{
    public const string SectionName = "Ai:Gemini";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Optional separate model for vision/image tasks (e.g., gemini-2.0-flash).
    /// If not set, falls back to <see cref="Model"/>.
    /// </summary>
    public string? VisionModel { get; set; }

    public string BaseUrl { get; set; } = null!;
    public int TimeoutSeconds { get; set; }
}

/// <summary>
/// Configuration for AI provider selection.
/// Bound from "Ai:Providers" config section. All defaults in appsettings.common.json.
/// </summary>
public class AiProviderSettings
{
    public const string SectionName = "Ai:Providers";

    public string LanguageDetectorProvider { get; set; } = null!;
    public string CaptionGeneratorProvider { get; set; } = null!;
}

