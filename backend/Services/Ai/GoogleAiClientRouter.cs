using Microsoft.Extensions.Caching.Memory;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Routes AI requests to the appropriate client based on the configured model.
/// - gemma-* models → GemmaTextClient (no JSON mode, no vision)
/// - gemini-* models (and others) → GeminiTextClient (JSON mode, vision supported)
///
/// This router is registered as IGeminiClient in DI and delegates to the appropriate implementation.
/// </summary>
public class GoogleAiClientRouter : IGeminiClient
{
    private readonly IGeminiClient _client;
    private readonly IGeminiClient _visionClient;
    private readonly ILogger<GoogleAiClientRouter> _logger;

    public GoogleAiClientRouter(
        HttpClient httpClient,
        GeminiSettings settings,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<GoogleAiClientRouter>();

        var isGemmaModel = IsGemmaModel(settings.Model);

        if (isGemmaModel)
        {
            _logger.LogInformation(
                "Detected Gemma model '{Model}'. Using GemmaTextClient (no JSON mode, no vision).",
                settings.Model);
            _client = new GemmaTextClient(
                httpClient,
                settings,
                cache,
                loggerFactory.CreateLogger<GemmaTextClient>());
        }
        else
        {
            _logger.LogInformation(
                "Detected Gemini model '{Model}'. Using GeminiTextClient (JSON mode enabled, vision supported).",
                settings.Model);
            _client = new GeminiTextClient(
                httpClient,
                settings,
                cache,
                loggerFactory.CreateLogger<GeminiTextClient>());
        }

        // Vision client: use dedicated VisionModel if configured, otherwise fall back to primary client
        if (!string.IsNullOrEmpty(settings.VisionModel))
        {
            var visionSettings = new GeminiSettings
            {
                ApiKey = settings.ApiKey,
                Model = settings.VisionModel,
                BaseUrl = settings.BaseUrl,
                TimeoutSeconds = settings.TimeoutSeconds
            };
            _logger.LogInformation(
                "Using dedicated vision model '{VisionModel}' for image analysis.",
                settings.VisionModel);
            _visionClient = new GeminiTextClient(
                httpClient,
                visionSettings,
                cache,
                loggerFactory.CreateLogger<GeminiTextClient>());
        }
        else
        {
            _visionClient = _client;
        }
    }

    /// <summary>
    /// Determines if the model is a Gemma model based on its name.
    /// Gemma models start with "gemma-" (e.g., gemma-3-27b-it).
    /// </summary>
    private static bool IsGemmaModel(string modelName)
    {
        return modelName.StartsWith("gemma-", StringComparison.OrdinalIgnoreCase) ||
               modelName.StartsWith("gemma2-", StringComparison.OrdinalIgnoreCase);
    }

    public Task<AiTextVariantsResponse> GenerateVariantsAsync(
        AiTextAction action,
        AiPlatform platform,
        string text,
        AiTone? tone,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        return _client.GenerateVariantsAsync(action, platform, text, tone, language, voiceProfile, cancellationToken);
    }

    public Task<AiHashtagsResponse> GenerateHashtagsAsync(
        AiPlatform platform,
        string text,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        return _client.GenerateHashtagsAsync(platform, text, language, voiceProfile, cancellationToken);
    }

    public Task<AiPreFlightResponse> RunPreFlightCheckAsync(
        AiPlatform platform,
        string text,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        return _client.RunPreFlightCheckAsync(platform, text, language, voiceProfile, cancellationToken);
    }

    public Task<AiGenerateVariantsResponse> GenerateCreatorVariantsAsync(
        AiGenerateVariantsRequest request,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default)
    {
        return _client.GenerateCreatorVariantsAsync(request, voiceProfile, cancellationToken);
    }

    public Task<AiMediaCaptionIdeasResponse> GenerateImageCaptionIdeasAsync(
        byte[] imageBytes,
        string imageMimeType,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default)
    {
        return _visionClient.GenerateImageCaptionIdeasAsync(imageBytes, imageMimeType, platform, existingText, language, cancellationToken);
    }

    public Task<AiImageQualityCheckResponse> CheckImageQualityAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        return _visionClient.CheckImageQualityAsync(imageBytes, imageMimeType, cancellationToken);
    }

    public Task<AiAltTextResponse> GenerateAltTextAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default)
    {
        return _visionClient.GenerateAltTextAsync(imageBytes, imageMimeType, cancellationToken);
    }
}
