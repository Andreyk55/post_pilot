namespace PostPilot.Api.Services.Ai;

public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Optional separate model for vision/image tasks (e.g., gemini-2.0-flash).
    /// If not set, falls back to <see cref="Model"/>.
    /// </summary>
    public string? VisionModel { get; set; }

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Configuration for AI provider selection.
/// </summary>
public class AiProviderSettings
{
    public string LanguageDetectorProvider { get; set; } = "gemini";
    public string CaptionGeneratorProvider { get; set; } = "gemini";
}

