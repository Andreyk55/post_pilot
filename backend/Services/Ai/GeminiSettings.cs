namespace PostPilot.Api.Services.Ai;

public class GeminiSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

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

