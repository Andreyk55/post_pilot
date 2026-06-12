namespace PostPilot.Api.Services.Media;

/// <summary>
/// Phase 3: produces an Instagram-safe JPEG derivative from a PNG original.
/// See <see cref="InstagramDerivativeService"/> for the conversion rules.
/// </summary>
public interface IInstagramDerivativeService
{
    /// <summary>
    /// True when an upload with this content type should get a JPEG derivative.
    /// Only PNG qualifies; JPEG is already Instagram-safe and WebP is unsupported.
    /// </summary>
    bool ShouldGenerateForContentType(string? contentType);

    /// <summary>
    /// Decodes the PNG <paramref name="pngSource"/> and produces an Instagram-safe JPEG:
    /// downscaled to at most 1440px wide (aspect preserved, never upscaled, never cropped),
    /// with transparency flattened onto a white background. Does NOT validate or fix the
    /// aspect ratio; that remains the validation gate's job.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source is not a PNG.</exception>
    Task<InstagramDerivativeResult> GenerateAsync(Stream pngSource, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes the deterministic derivative storage key for an original key. The derivative
    /// lives in the same media folder and keeps the original base filename with a
    /// <c>.jpg</c> extension (e.g. <c>.../media/{mediaId}/holiday.jpg</c>).
    /// </summary>
    string BuildDerivativeKey(string originalStorageKey);
}

/// <summary>
/// Result of a successful derivative generation. <see cref="JpegBytes"/> is a readable,
/// rewound stream the caller is responsible for disposing.
/// </summary>
public record InstagramDerivativeResult(
    Stream JpegBytes,
    int Width,
    int Height,
    long SizeBytes,
    string MimeType);
