using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Gemini-based implementation of multilingual caption generation.
/// Supports both Gemini (with JSON mode) and Gemma (without JSON mode) models.
/// </summary>
public class GeminiCaptionGenerator : GoogleAiClientBase, ICaptionGenerator
{
    public GeminiCaptionGenerator(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILogger<GeminiCaptionGenerator> logger)
        : base(httpClient, settings, cache, logger)
    {
    }

    // Gemma models don't support JSON mode
    protected override bool SupportsJsonMode => !Settings.Model.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);
    protected override bool SupportsVision => false;
    protected override string ClientName => "GeminiCaptionGenerator";

    public async Task<CaptionGenerateResult> GenerateAsync(
        Services.Ai.CaptionGenerateRequest request,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildCaptionPrompt(request);
        var responseText = await CallGenerateContentAsync(prompt, cancellationToken, maxOutputTokens: 4096);

        var result = ParseCaptionResponse(responseText);
        return result;
    }

    private string BuildCaptionPrompt(Services.Ai.CaptionGenerateRequest request)
    {
        var isSameLanguage = request.SourceLanguage.Equals(request.OutputLanguage, StringComparison.OrdinalIgnoreCase);
        var action = isSameLanguage ? "rewrite and improve" : "translate";

        var strictInstructions = request.StrictMeaning
            ? @"
CRITICAL PRESERVATION RULES (MUST FOLLOW):
1. Preserve ALL numbers exactly as they appear (e.g., 50, 3.5, 1,234)
2. Preserve ALL currency symbols and amounts exactly (e.g., $50, €100, ₪250)
3. Preserve ALL percentages exactly (e.g., 25%, 10% off)
4. Preserve ALL dates and times exactly
5. Preserve ALL URLs exactly (do not modify or remove any part)
6. Preserve ALL hashtags exactly (e.g., #sale, #חדש, #новинка)
7. Preserve ALL @mentions exactly (e.g., @username)
8. Preserve ALL brand names and product names exactly
9. Do NOT add new information, claims, or facts that are not in the source text
10. Do NOT remove any information from the source text

If any of these elements appear in the source text, they MUST appear unchanged in the output."
            : "Try to preserve numbers, URLs, hashtags, @mentions, and brand names when possible.";

        var voiceContext = BuildVoiceProfileContext(request.VoiceProfile, request.KeepBrandVoice);

        var platformContext = request.Platform switch
        {
            AiPlatform.Facebook => "Facebook post",
            AiPlatform.Instagram => "Instagram post",
            AiPlatform.LinkedIn => "LinkedIn post",
            AiPlatform.X => "X (Twitter) post",
            _ => "social media post"
        };

        var variantsInstruction = request.Variants > 1
            ? $"Generate {request.Variants} different variants."
            : "Generate 1 variant.";

        return $@"You are a professional social media content {action} specialist.

SOURCE TEXT (in {GetLanguageName(request.SourceLanguage)}):
{request.Text}

TASK:
{(isSameLanguage ? $"Rewrite and improve this text in {GetLanguageName(request.OutputLanguage)}, making it more engaging for {platformContext}." : $"Translate this text from {GetLanguageName(request.SourceLanguage)} to {GetLanguageName(request.OutputLanguage)} for {platformContext}.")}
{variantsInstruction}

{strictInstructions}

{voiceContext}

Respond with a JSON object containing:
- captions: An array of {request.Variants} string(s)
- warnings: An array of strings (use if you couldn't preserve something important, or if source text is unclear)

Example response:
{{
  ""captions"": [
    ""Caption variant 1"",
    ""Caption variant 2""
  ],
  ""warnings"": []
}}

If you detect any issues (e.g., ambiguous text, difficult-to-preserve elements), add them to the warnings array.
If you cannot preserve a required element exactly, add a warning explaining what was changed.";
    }

    private string BuildVoiceProfileContext(AiVoiceProfile? profile, bool keepBrandVoice)
    {
        if (profile == null || !keepBrandVoice)
            return "";

        var context = "BRAND VOICE PROFILE:\n";

        if (!string.IsNullOrWhiteSpace(profile.ExamplePosts))
        {
            context += $"\nExample posts:\n{profile.ExamplePosts}\n";
        }

        if (!string.IsNullOrWhiteSpace(profile.DoRules))
        {
            context += $"\nDO:\n{profile.DoRules}\n";
        }

        if (!string.IsNullOrWhiteSpace(profile.DontRules))
        {
            context += $"\nDON'T:\n{profile.DontRules}\n";
        }

        if (!string.IsNullOrWhiteSpace(profile.BannedWords))
        {
            context += $"\nBanned words/phrases: {profile.BannedWords}\n";
        }

        context += "\nApply this brand voice to the generated captions.\n";

        return context;
    }

    private string GetLanguageName(string languageCode)
    {
        return languageCode.ToLower() switch
        {
            "en" => "English",
            "he" => "Hebrew",
            "ru" => "Russian",
            "ar" => "Arabic",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ja" => "Japanese",
            "zh" => "Chinese",
            "ko" => "Korean",
            "hi" => "Hindi",
            "tr" => "Turkish",
            "pl" => "Polish",
            "nl" => "Dutch",
            "sv" => "Swedish",
            "no" => "Norwegian",
            "da" => "Danish",
            "fi" => "Finnish",
            _ => languageCode
        };
    }

    private CaptionGenerateResult ParseCaptionResponse(string responseText)
    {
        try
        {
            // Extract JSON from potential markdown code fences (Gemma models return ```json ... ```)
            var json = ExtractJson(responseText);
            var jsonDoc = JsonDocument.Parse(json);
            var root = jsonDoc.RootElement;

            var captions = new List<string>();
            if (root.TryGetProperty("captions", out var captionsElement))
            {
                foreach (var caption in captionsElement.EnumerateArray())
                {
                    var text = caption.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        captions.Add(text.Trim());
                    }
                }
            }

            var warnings = new List<string>();
            if (root.TryGetProperty("warnings", out var warningsElement))
            {
                foreach (var warning in warningsElement.EnumerateArray())
                {
                    var text = warning.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        warnings.Add(text.Trim());
                    }
                }
            }

            return new CaptionGenerateResult(captions.ToArray(), warnings.ToArray());
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse caption generation response: {Response}", responseText);
            throw new InvalidOperationException("Failed to parse AI response", ex);
        }
    }
}
