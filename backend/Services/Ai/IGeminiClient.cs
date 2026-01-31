using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

public interface IGeminiClient
{
    Task<AiTextVariantsResponse> GenerateVariantsAsync(
        AiTextAction action,
        AiPlatform platform,
        string text,
        AiTone? tone,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default);

    Task<AiHashtagsResponse> GenerateHashtagsAsync(
        AiPlatform platform,
        string text,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default);

    Task<AiPreFlightResponse> RunPreFlightCheckAsync(
        AiPlatform platform,
        string text,
        string language,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default);

    // Vision API methods for media AI

    /// <summary>
    /// Generates caption ideas for an image, considering platform and existing post text.
    /// </summary>
    Task<AiMediaCaptionIdeasResponse> GenerateImageCaptionIdeasAsync(
        byte[] imageBytes,
        string imageMimeType,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a quality check on an image, returning a score and issues.
    /// </summary>
    Task<AiImageQualityCheckResponse> CheckImageQualityAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates alt text for an image.
    /// </summary>
    Task<AiAltTextResponse> GenerateAltTextAsync(
        byte[] imageBytes,
        string imageMimeType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates text variants with full control options (goal, tone, length, include flags).
    /// </summary>
    Task<AiGenerateVariantsResponse> GenerateCreatorVariantsAsync(
        AiGenerateVariantsRequest request,
        AiVoiceProfile? voiceProfile = null,
        CancellationToken cancellationToken = default);
}
