namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Provider-agnostic interface for language detection.
/// </summary>
public interface ILanguageDetector
{
    /// <summary>
    /// Detects the language of the given text.
    /// </summary>
    Task<LanguageDetectResult> DetectAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of language detection.
/// </summary>
public record LanguageDetectResult(
    string LanguageCode,
    double Confidence,
    bool IsReliable);
