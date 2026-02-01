using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Provider-agnostic interface for multilingual caption generation and translation.
/// </summary>
public interface ICaptionGenerator
{
    /// <summary>
    /// Generates or translates captions based on the request parameters.
    /// </summary>
    Task<CaptionGenerateResult> GenerateAsync(
        CaptionGenerateRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request for caption generation/translation.
/// </summary>
public record CaptionGenerateRequest(
    string Text,
    string SourceLanguage,
    string OutputLanguage,
    AiPlatform Platform,
    int Variants,
    bool StrictMeaning,
    bool KeepBrandVoice,
    AiVoiceProfile? VoiceProfile);

/// <summary>
/// Result of caption generation from the AI provider.
/// </summary>
public record CaptionGenerateResult(
    string[] Captions,
    string[] Warnings);
