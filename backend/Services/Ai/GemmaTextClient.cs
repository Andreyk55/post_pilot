using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Client for Gemma models (e.g., gemma-3-27b-it).
/// Does NOT support JSON mode (ResponseMimeType must be omitted).
/// Does NOT support vision - vision calls will throw an error.
///
/// Gemma models still receive JSON-requesting prompts and parse JSON from responses,
/// but without the guaranteed JSON mode, parsing must be resilient to non-JSON output.
/// </summary>
public class GemmaTextClient : GoogleAiClientBase, IGeminiClient
{
    public GemmaTextClient(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILogger<GemmaTextClient> logger)
        : base(httpClient, settings, cache, logger)
    {
    }

    /// <summary>
    /// Gemma does not support JSON mode. ResponseMimeType will be omitted from requests.
    /// </summary>
    protected override bool SupportsJsonMode => false;

    /// <summary>
    /// Gemma models do not support vision/image inputs.
    /// Vision calls will throw a clear error directing users to use a Gemini model.
    /// </summary>
    protected override bool SupportsVision => false;

    protected override string ClientName => "Gemma";

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

        try
        {
            var result = ParseVariantsResponse(responseText, action);
            Cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (Exception ex) when (ex is not GeminiApiException)
        {
            Logger.LogWarning("Failed to parse Gemma variants response. Raw response: {Response}", responseText);
            throw new GeminiApiException("Failed to parse AI response. The model may not have returned valid JSON.", 500);
        }
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

        try
        {
            var result = ParseHashtagsResponse(responseText);
            Cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (Exception ex) when (ex is not GeminiApiException)
        {
            Logger.LogWarning("Failed to parse Gemma hashtags response. Raw response: {Response}", responseText);
            throw new GeminiApiException("Failed to parse AI response. The model may not have returned valid JSON.", 500);
        }
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

        try
        {
            var result = ParsePreFlightResponse(responseText);
            Cache.Set(cacheKey, result, CacheDuration);
            return result;
        }
        catch (Exception ex) when (ex is not GeminiApiException)
        {
            Logger.LogWarning("Failed to parse Gemma pre-flight response. Raw response: {Response}", responseText);
            throw new GeminiApiException("Failed to parse AI response. The model may not have returned valid JSON.", 500);
        }
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

        try
        {
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
        catch (Exception ex) when (ex is not GeminiApiException)
        {
            Logger.LogWarning("Failed to parse Gemma creator variants response. Raw response: {Response}", responseText);
            throw new GeminiApiException("Failed to parse AI response. The model may not have returned valid JSON.", 500);
        }
    }

    /// <summary>
    /// Vision is not supported by Gemma models.
    /// Throws a clear error directing users to use a Gemini model for image processing.
    /// </summary>
    public Task<AiMediaCaptionIdeasResponse> GenerateImageCaptionIdeasAsync(
        byte[] imageBytes,
        string imageMimeType,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default)
    {
        throw new GeminiApiException(
            $"Vision (image processing) is not supported by Gemma model '{Settings.Model}'. " +
            "Please configure a Gemini model (e.g., gemini-2.0-flash) for image analysis features.",
            400);
    }

    /// <summary>
    /// Vision is not supported by Gemma models.
    /// Throws a clear error directing users to use a Gemini model for image processing.
    /// </summary>
    public Task<AiImageQualityCheckResponse> CheckImageQualityAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        throw new GeminiApiException(
            $"Vision (image processing) is not supported by Gemma model '{Settings.Model}'. " +
            "Please configure a Gemini model (e.g., gemini-2.0-flash) for image analysis features.",
            400);
    }

    /// <summary>
    /// Vision is not supported by Gemma models.
    /// Throws a clear error directing users to use a Gemini model for image processing.
    /// </summary>
    public Task<AiAltTextResponse> GenerateAltTextAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        throw new GeminiApiException(
            $"Vision (image processing) is not supported by Gemma model '{Settings.Model}'. " +
            "Please configure a Gemini model (e.g., gemini-2.0-flash) for image analysis features.",
            400);
    }
}
