using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Service for orchestrating AI operations on media assets.
/// </summary>
public interface IMediaAiService
{
    /// <summary>
    /// Generates caption ideas for an image.
    /// </summary>
    Task<AiMediaCaptionIdeasResponse> GenerateImageCaptionIdeasAsync(
        string assetUrl,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a quality check on an image.
    /// </summary>
    Task<AiImageQualityCheckResponse> CheckImageQualityAsync(
        string assetUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates alt text for an image.
    /// </summary>
    Task<AiAltTextResponse> GenerateAltTextAsync(
        string assetUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates caption ideas for a video (based on first frame).
    /// </summary>
    Task<AiMediaCaptionIdeasResponse> GenerateVideoCaptionIdeasAsync(
        string assetUrl,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suggests thumbnail frames for a video.
    /// Note: This method requires FFmpeg and is not available in Lambda.
    /// Use ProcessClientExtractedFramesAsync instead for serverless environments.
    /// </summary>
    Task<AiThumbnailSuggestResponse> SuggestThumbnailsAsync(
        string assetUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Processes frames that were extracted client-side.
    /// Saves the frames and returns URLs for thumbnail selection.
    /// This approach works in Lambda without FFmpeg dependency.
    /// </summary>
    Task<AiThumbnailSuggestResponse> ProcessClientExtractedFramesAsync(
        List<ClientExtractedFrame> frames,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates caption ideas for a video using a pre-extracted frame.
    /// Frame is extracted client-side and sent as base64 data URL.
    /// This approach works in Lambda without FFmpeg dependency.
    /// </summary>
    Task<AiMediaCaptionIdeasResponse> GenerateVideoCaptionIdeasFromFrameAsync(
        string frameData,
        AiPlatform platform,
        string? existingText,
        string language,
        CancellationToken cancellationToken = default);
}
