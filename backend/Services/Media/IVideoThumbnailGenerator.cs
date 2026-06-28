namespace PostPilot.Api.Services.Media;

/// <summary>
/// Generates a small static preview image from a video using a local toolchain.
/// Implementations must not send video bytes to external services.
/// </summary>
public interface IVideoThumbnailGenerator
{
    Task<VideoThumbnailResult> GenerateAsync(
        string sourceVideoPath,
        string outputImagePath,
        int maxWidth,
        CancellationToken cancellationToken = default);
}

public sealed record VideoThumbnailResult(
    string MimeType,
    int Width,
    int Height,
    long SizeBytes
);