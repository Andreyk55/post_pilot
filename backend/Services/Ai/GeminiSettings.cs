namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Configuration for Gemini AI service.
/// Non-secret values (BaseUrl, TimeoutSeconds) from "Ai:Gemini" config section.
/// Secrets (ApiKey, Model, VisionModel) from environment variables only.
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

