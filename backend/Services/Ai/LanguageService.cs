namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Application service for language detection with fallback support.
/// </summary>
public class LanguageService
{
    private readonly ILanguageDetector _detector;
    private readonly ILogger<LanguageService> _logger;

    public LanguageService(ILanguageDetector detector, ILogger<LanguageService> logger)
    {
        _detector = detector;
        _logger = logger;
    }

    public async Task<LanguageDetectResult> DetectLanguageAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new LanguageDetectResult("en", 0.0, false);
        }

        try
        {
            var result = await _detector.DetectAsync(text, cancellationToken);
            _logger.LogDebug("Language detected: {Language} (confidence: {Confidence}, reliable: {IsReliable})",
                result.LanguageCode, result.Confidence, result.IsReliable);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Language detection failed, falling back to English");
            return new LanguageDetectResult("en", 0.0, false);
        }
    }
}
