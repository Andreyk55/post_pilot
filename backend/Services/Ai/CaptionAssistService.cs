using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Application service for multilingual caption assistance with strict meaning enforcement.
/// </summary>
public class CaptionAssistService
{
    private readonly LanguageService _languageService;
    private readonly ICaptionGenerator _captionGenerator;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CaptionAssistService> _logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);

    // Regex patterns for strict validation
    private static readonly Regex NumberPattern = new Regex(@"\b\d+([.,]\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex CurrencyPattern = new Regex(@"[$€£¥₪₽]\s*\d+([.,]\d+)?|\d+([.,]\d+)?\s*[$€£¥₪₽]", RegexOptions.Compiled);
    private static readonly Regex PercentagePattern = new Regex(@"\d+([.,]\d+)?\s*%", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new Regex(@"https?://[^\s]+|www\.[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HashtagPattern = new Regex(@"#\w+", RegexOptions.Compiled);
    private static readonly Regex MentionPattern = new Regex(@"@\w+", RegexOptions.Compiled);

    public CaptionAssistService(
        LanguageService languageService,
        ICaptionGenerator captionGenerator,
        IMemoryCache cache,
        ILogger<CaptionAssistService> logger)
    {
        _languageService = languageService;
        _captionGenerator = captionGenerator;
        _cache = cache;
        _logger = logger;
    }

    public async Task<(LanguageDetectResult detection, string[] captions, string[] warnings)> GenerateCaptionsAsync(
        string text,
        AiPlatform platform,
        string? outputLanguage,
        int variants,
        bool strictMeaning,
        bool keepBrandVoice,
        AiVoiceProfile? voiceProfile,
        CancellationToken cancellationToken = default)
    {
        // Detect source language
        var detection = await _languageService.DetectLanguageAsync(text, cancellationToken);

        // Determine output language (default to source language)
        var targetLanguage = string.IsNullOrWhiteSpace(outputLanguage) || outputLanguage == "auto"
            ? detection.LanguageCode
            : outputLanguage;

        // Build cache key
        var cacheKey = BuildCacheKey(text, detection.LanguageCode, targetLanguage, platform, strictMeaning, keepBrandVoice, variants, voiceProfile);

        if (_cache.TryGetValue(cacheKey, out (string[], string[])? cached) && cached.HasValue)
        {
            _logger.LogDebug("Cache hit for caption generation");
            return (detection, cached.Value.Item1, cached.Value.Item2);
        }

        // Generate captions
        var request = new Services.Ai.CaptionGenerateRequest(
            text,
            detection.LanguageCode,
            targetLanguage,
            platform,
            variants,
            strictMeaning,
            keepBrandVoice,
            voiceProfile);

        var result = await _captionGenerator.GenerateAsync(request, cancellationToken);

        // Validate strict meaning if enabled
        var validatedCaptions = result.Captions;
        var allWarnings = result.Warnings.ToList();

        if (strictMeaning && result.Captions.Length > 0)
        {
            var validationResult = await ValidateStrictMeaningAsync(
                text,
                result.Captions,
                detection.LanguageCode,
                targetLanguage,
                platform,
                keepBrandVoice,
                voiceProfile,
                cancellationToken);

            validatedCaptions = validationResult.captions;
            allWarnings.AddRange(validationResult.warnings);
        }

        var finalResult = (validatedCaptions, allWarnings.ToArray());
        _cache.Set(cacheKey, finalResult, CacheDuration);

        return (detection, validatedCaptions, allWarnings.ToArray());
    }

    private async Task<(string[] captions, string[] warnings)> ValidateStrictMeaningAsync(
        string sourceText,
        string[] captions,
        string sourceLanguage,
        string targetLanguage,
        AiPlatform platform,
        bool keepBrandVoice,
        AiVoiceProfile? voiceProfile,
        CancellationToken cancellationToken)
    {
        var sourceNumbers = ExtractNumbers(sourceText);
        var sourceCurrencies = ExtractCurrencies(sourceText);
        var sourcePercentages = ExtractPercentages(sourceText);
        var sourceUrls = ExtractUrls(sourceText);
        var sourceHashtags = ExtractHashtags(sourceText);
        var sourceMentions = ExtractMentions(sourceText);

        var validatedCaptions = new List<string>();
        var warnings = new List<string>();

        foreach (var caption in captions)
        {
            var violations = new List<string>();

            // Check numbers
            if (!ValidateExactMatch(sourceNumbers, ExtractNumbers(caption)))
                violations.Add("numbers");

            // Check currencies
            if (!ValidateExactMatch(sourceCurrencies, ExtractCurrencies(caption)))
                violations.Add("currency amounts");

            // Check percentages
            if (!ValidateExactMatch(sourcePercentages, ExtractPercentages(caption)))
                violations.Add("percentages");

            // Check URLs
            if (!ValidateExactMatch(sourceUrls, ExtractUrls(caption)))
                violations.Add("URLs");

            // Check hashtags
            if (!ValidateExactMatch(sourceHashtags, ExtractHashtags(caption)))
                violations.Add("hashtags");

            // Check mentions
            if (!ValidateExactMatch(sourceMentions, ExtractMentions(caption)))
                violations.Add("@mentions");

            if (violations.Count > 0)
            {
                _logger.LogWarning("Caption validation failed: {Violations}", string.Join(", ", violations));

                // Retry with repair prompt
                var repairedCaption = await RepairCaptionAsync(
                    sourceText,
                    caption,
                    violations,
                    sourceLanguage,
                    targetLanguage,
                    platform,
                    keepBrandVoice,
                    voiceProfile,
                    cancellationToken);

                // Validate again
                var stillViolated = new List<string>();
                if (!ValidateExactMatch(sourceNumbers, ExtractNumbers(repairedCaption)))
                    stillViolated.Add("numbers");
                if (!ValidateExactMatch(sourceCurrencies, ExtractCurrencies(repairedCaption)))
                    stillViolated.Add("currency");
                if (!ValidateExactMatch(sourcePercentages, ExtractPercentages(repairedCaption)))
                    stillViolated.Add("percentages");
                if (!ValidateExactMatch(sourceUrls, ExtractUrls(repairedCaption)))
                    stillViolated.Add("URLs");
                if (!ValidateExactMatch(sourceHashtags, ExtractHashtags(repairedCaption)))
                    stillViolated.Add("hashtags");
                if (!ValidateExactMatch(sourceMentions, ExtractMentions(repairedCaption)))
                    stillViolated.Add("@mentions");

                if (stillViolated.Count > 0)
                {
                    warnings.Add($"Unable to preserve exact: {string.Join(", ", stillViolated)}");
                    // Use repaired version anyway (best effort)
                    validatedCaptions.Add(repairedCaption);
                }
                else
                {
                    validatedCaptions.Add(repairedCaption);
                }
            }
            else
            {
                validatedCaptions.Add(caption);
            }
        }

        return (validatedCaptions.ToArray(), warnings.ToArray());
    }

    private async Task<string> RepairCaptionAsync(
        string sourceText,
        string violatedCaption,
        List<string> violations,
        string sourceLanguage,
        string targetLanguage,
        AiPlatform platform,
        bool keepBrandVoice,
        AiVoiceProfile? voiceProfile,
        CancellationToken cancellationToken)
    {
        try
        {
            var repairPrompt = $@"The following caption does not exactly preserve the required elements from the source text.

SOURCE TEXT:
{sourceText}

GENERATED CAPTION (with violations):
{violatedCaption}

VIOLATIONS:
The caption did not preserve these elements exactly: {string.Join(", ", violations)}

TASK:
Fix the caption by ensuring ALL of the following from the source text are preserved EXACTLY:
- Numbers (including decimals, thousands separators)
- Currency symbols and amounts
- Percentages
- URLs
- Hashtags (with # symbol)
- @mentions

Respond with ONLY the corrected caption text, no JSON, no explanations.";

            var request = new Services.Ai.CaptionGenerateRequest(
                repairPrompt,
                sourceLanguage,
                targetLanguage,
                platform,
                1,
                true, // strictMeaning
                keepBrandVoice,
                voiceProfile);

            // For repair, we call the generator but parse plain text response
            var repairRequest = await _captionGenerator.GenerateAsync(request, cancellationToken);
            
            // Return first caption or fallback to original violated caption
            return repairRequest.Captions.Length > 0 ? repairRequest.Captions[0] : violatedCaption;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to repair caption");
            return violatedCaption;
        }
    }

    private bool ValidateExactMatch(HashSet<string> source, HashSet<string> target)
    {
        // Both must have same count and all elements must match
        if (source.Count != target.Count)
            return false;

        return source.SetEquals(target);
    }

    private HashSet<string> ExtractNumbers(string text)
    {
        var matches = NumberPattern.Matches(text);
        return new HashSet<string>(matches.Select(m => m.Value), StringComparer.Ordinal);
    }

    private HashSet<string> ExtractCurrencies(string text)
    {
        var matches = CurrencyPattern.Matches(text);
        return new HashSet<string>(matches.Select(m => m.Value), StringComparer.Ordinal);
    }

    private HashSet<string> ExtractPercentages(string text)
    {
        var matches = PercentagePattern.Matches(text);
        return new HashSet<string>(matches.Select(m => m.Value), StringComparer.Ordinal);
    }

    private HashSet<string> ExtractUrls(string text)
    {
        var matches = UrlPattern.Matches(text);
        return new HashSet<string>(matches.Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> ExtractHashtags(string text)
    {
        var matches = HashtagPattern.Matches(text);
        return new HashSet<string>(matches.Select(m => m.Value), StringComparer.Ordinal);
    }

    private HashSet<string> ExtractMentions(string text)
    {
        var matches = MentionPattern.Matches(text);
        return new HashSet<string>(matches.Select(m => m.Value), StringComparer.Ordinal);
    }

    private string BuildCacheKey(
        string text,
        string sourceLanguage,
        string targetLanguage,
        AiPlatform platform,
        bool strictMeaning,
        bool keepBrandVoice,
        int variants,
        AiVoiceProfile? voiceProfile)
    {
        var textHash = ComputeHash(text);
        var voiceProfileId = voiceProfile?.Id.ToString() ?? "none";

        return $"Caption:{textHash}:{sourceLanguage}:{targetLanguage}:{platform}:{strictMeaning}:{keepBrandVoice}:{variants}:{voiceProfileId}";
    }

    private string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16]; // First 16 chars
    }
}
