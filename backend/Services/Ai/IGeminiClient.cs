using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Ai;

public interface IGeminiClient
{
    Task<AiTextVariantsResponse> GenerateVariantsAsync(
        AiTextAction action,
        AiPlatform platform,
        string text,
        AiTone? tone,
        string language,
        CancellationToken cancellationToken = default);

    Task<AiHashtagsResponse> GenerateHashtagsAsync(
        AiPlatform platform,
        string text,
        string language,
        CancellationToken cancellationToken = default);

    Task<AiPreFlightResponse> RunPreFlightCheckAsync(
        AiPlatform platform,
        string text,
        string language,
        CancellationToken cancellationToken = default);
}
