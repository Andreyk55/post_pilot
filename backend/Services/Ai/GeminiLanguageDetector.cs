using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Gemini-based implementation of language detection.
/// Supports both Gemini (with JSON mode) and Gemma (without JSON mode) models.
/// </summary>
public class GeminiLanguageDetector : GoogleAiClientBase, ILanguageDetector
{
    public GeminiLanguageDetector(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILogger<GeminiLanguageDetector> logger,
        AiCacheOptions cacheOptions)
        : base(httpClient, settings, cache, logger,
              TimeSpan.FromMinutes(cacheOptions.LanguageDetectionMinutes))
    {
    }

    // Gemma models don't support JSON mode
    protected override bool SupportsJsonMode => !Settings.Model.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);
    protected override bool SupportsVision => false;
    protected override string ClientName => "GeminiLanguageDetector";

    public async Task<LanguageDetectResult> DetectAsync(string text, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"LanguageDetect:{ComputeSimpleHash(text)}";

        if (Cache.TryGetValue(cacheKey, out LanguageDetectResult? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for language detection");
            return cached;
        }

        var prompt = BuildDetectionPrompt(text);
        var responseText = await CallGenerateContentAsync(prompt, cancellationToken, maxOutputTokens: 256);

        var result = ParseDetectionResponse(responseText, text.Length);

        Cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private string ComputeSimpleHash(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes)[..16];
    }

    private string BuildDetectionPrompt(string text)
    {
        return $@"Detect the language of the following text.

TEXT:
{text}

Respond with a JSON object containing:
- languageCode: ISO 639-1 language code (e.g., ""en"", ""he"", ""ru"", ""ar"", ""es"", ""fr"", ""de"", ""it"", ""pt"", ""ja"", ""zh"", ""ko"", ""hi"", ""tr"", ""pl"", ""nl"", ""sv"", ""no"", ""da"", ""fi"")
- confidence: A number between 0.0 and 1.0 indicating detection confidence

Example response:
{{
  ""languageCode"": ""he"",
  ""confidence"": 0.95
}}

Important:
- If the text is very short (< 10 characters), still provide your best guess but lower confidence
- If the text is mixed-language, return the primary/dominant language
- For code, URLs, or non-linguistic content, return ""en"" with low confidence";
    }

    private LanguageDetectResult ParseDetectionResponse(string responseText, int textLength)
    {
        try
        {
            // Extract JSON from potential markdown code fences (Gemma models return ```json ... ```)
            var json = ExtractJson(responseText);
            var jsonDoc = JsonDocument.Parse(json);
            var root = jsonDoc.RootElement;

            var languageCode = root.GetProperty("languageCode").GetString() ?? "en";
            var confidence = root.GetProperty("confidence").GetDouble();

            // Reliability heuristic: text must be at least 10 chars and confidence > 0.6
            var isReliable = textLength >= 10 && confidence >= 0.6;

            return new LanguageDetectResult(languageCode, confidence, isReliable);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse language detection response: {Response}", responseText);
            // Fallback to English
            return new LanguageDetectResult("en", 0.0, false);
        }
    }
}
