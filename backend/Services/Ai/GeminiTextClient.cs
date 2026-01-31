using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Client for Gemini models (e.g., gemini-2.5-flash).
/// Supports JSON mode (ResponseMimeType = "application/json") and vision.
/// </summary>
public class GeminiTextClient : GoogleAiClientBase, IGeminiClient
{
    public GeminiTextClient(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILogger<GeminiTextClient> logger)
        : base(httpClient, settings, cache, logger)
    {
    }

    protected override bool SupportsJsonMode => true;
    protected override bool SupportsVision => true;
    protected override string ClientName => "Gemini";

    public async Task<AiTextVariantsResponse> GenerateVariantsAsync(
        AiTextAction action,
        AiPlatform platform,
        string text,
        AiTone? tone,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKeyWithVoiceProfile(action.ToString(), platform.ToString(), tone?.ToString() ?? "", language, text, voiceProfile);

        if (Cache.TryGetValue(cacheKey, out AiTextVariantsResponse? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for variants: {Action}, {Platform}", action, platform);
            return cached;
        }

        var prompt = BuildVariantsPromptWithVoice(action, platform, text, tone, language, voiceProfile);
        var responseText = await CallGenerateContentAsync(prompt, cancellationToken);
        var result = ParseVariantsResponse(responseText, action);

        Cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiHashtagsResponse> GenerateHashtagsAsync(
        AiPlatform platform,
        string text,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKeyWithVoiceProfile("Hashtags", platform.ToString(), "", language, text, voiceProfile);

        if (Cache.TryGetValue(cacheKey, out AiHashtagsResponse? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for hashtags: {Platform}", platform);
            return cached;
        }

        var prompt = BuildHashtagsPromptWithVoice(platform, text, language, voiceProfile);
        var responseText = await CallGenerateContentAsync(prompt, cancellationToken);
        var result = ParseHashtagsResponse(responseText);

        Cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiPreFlightResponse> RunPreFlightCheckAsync(
        AiPlatform platform,
        string text,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKeyWithVoiceProfile("PreFlight", platform.ToString(), "", language, text, voiceProfile);

        if (Cache.TryGetValue(cacheKey, out AiPreFlightResponse? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for pre-flight: {Platform}", platform);
            return cached;
        }

        var prompt = BuildPreFlightPromptWithVoice(platform, text, language, voiceProfile);
        var responseText = await CallGenerateContentAsync(prompt, cancellationToken, maxOutputTokens: 3072);
        var result = ParsePreFlightResponse(responseText);

        Cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiGenerateVariantsResponse> GenerateCreatorVariantsAsync(
        AiGenerateVariantsRequest request,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        var numToGenerate = request.RegenerateIndex.HasValue ? 1 : request.NumVariants;
        var skipCache = request.RegenerateIndex.HasValue;
        var cacheKey = BuildCreatorVariantsCacheKeyWithVoice(request, voiceProfile);

        if (!skipCache && Cache.TryGetValue(cacheKey, out AiGenerateVariantsResponse? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for creator variants: {Goal}, {Tone}, {Platform}",
                request.Goal, request.Tone, request.Platform);
            return cached;
        }

        var prompt = BuildCreatorVariantsPromptWithVoice(request, numToGenerate, voiceProfile);
        var responseText = await CallGenerateContentAsync(prompt, cancellationToken, maxOutputTokens: 4096);
        var result = ParseCreatorVariantsResponse(responseText, numToGenerate);

        if (result.Variants.Count < numToGenerate)
        {
            Logger.LogWarning("Expected {Expected} variants but got {Actual}", numToGenerate, result.Variants.Count);
        }

        if (!skipCache)
        {
            Cache.Set(cacheKey, result, CacheDuration);
        }

        return result;
    }

    public async Task<AiMediaCaptionIdeasResponse> GenerateImageCaptionIdeasAsync(
        byte[] imageBytes,
        string imageMimeType,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default)
    {
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes))[..16];
        var cacheKey = BuildCacheKey("ImageCaption", platform.ToString(), "", language, $"{imageHash}:{existingText ?? ""}");

        if (Cache.TryGetValue(cacheKey, out AiMediaCaptionIdeasResponse? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for image caption: {Platform}", platform);
            return cached;
        }

        var prompt = BuildImageCaptionPrompt(platform, existingText, language);
        var responseText = await CallVisionAsync(prompt, imageBytes, imageMimeType, maxOutputTokens: 512, cancellationToken: cancellationToken);
        var result = ParseImageCaptionResponse(responseText);

        Cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiImageQualityCheckResponse> CheckImageQualityAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes))[..16];
        var cacheKey = $"ai:ImageQuality:{imageHash}";

        if (Cache.TryGetValue(cacheKey, out AiImageQualityCheckResponse? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for image quality check");
            return cached;
        }

        var prompt = BuildImageQualityPrompt();
        var responseText = await CallVisionAsync(prompt, imageBytes, imageMimeType, maxOutputTokens: 512, cancellationToken: cancellationToken);
        var result = ParseImageQualityResponse(responseText);

        Cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    public async Task<AiAltTextResponse> GenerateAltTextAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        var imageHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(imageBytes))[..16];
        var cacheKey = $"ai:AltText:{imageHash}";

        if (Cache.TryGetValue(cacheKey, out AiAltTextResponse? cached) && cached != null)
        {
            Logger.LogDebug("Cache hit for alt text");
            return cached;
        }

        var prompt = BuildAltTextPrompt();
        var responseText = await CallVisionAsync(prompt, imageBytes, imageMimeType, maxOutputTokens: 256, cancellationToken: cancellationToken);
        var result = ParseAltTextResponse(responseText);

        Cache.Set(cacheKey, result, CacheDuration);
        return result;
    }
}
