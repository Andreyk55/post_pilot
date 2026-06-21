namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Configuration for Gemini AI service.
/// Bound from "Gemini" config section.
/// ApiKey, Model, and VisionModel are required from deployment environment.
/// </summary>
public class GeminiSettings
{
    public const string SectionName = "Gemini";

    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string VisionModel { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = null!;
    public int TimeoutSeconds { get; set; }
}

/// <summary>
/// Configuration for AI provider selection.
/// Bound from "Ai:Providers" config section.
/// </summary>
public class AiProviderSettings
{
    public const string SectionName = "Ai:Providers";

    public string LanguageDetectorProvider { get; set; } = null!;
    public string CaptionGeneratorProvider { get; set; } = null!;
}
