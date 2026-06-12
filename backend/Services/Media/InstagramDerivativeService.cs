using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Phase 3: generates an Instagram-safe JPEG derivative from a PNG original.
///
/// <para>
/// Meta accepts JPEG ONLY for Instagram (PNG/WebP are rejected at publish time). To let
/// users upload PNGs for Instagram, we convert PNG to JPEG server-side at upload-complete
/// time and store the result alongside the original (the original is never replaced -
/// Facebook and previews keep using it).
/// </para>
///
/// <para>Conversion rules (intentionally conservative - see Phase 3 spec):</para>
/// <list type="bullet">
///   <item>PNG to JPEG only. JPEG/WebP/video inputs return null (no derivative).</item>
///   <item>Preserve aspect ratio; never crop.</item>
///   <item>Downscale to max width <see cref="MaxWidth"/> when wider; never upscale.</item>
///   <item>Flatten transparency onto a safe background before encoding JPEG.</item>
///   <item>Does NOT auto-fix aspect ratio - an out-of-range image is still converted,
///         and the validation gate rejects it for Instagram afterwards.</item>
/// </list>
/// </summary>
public class InstagramDerivativeService : IInstagramDerivativeService
{
    /// <summary>Instagram downscales any width &gt; 1440px, so we pre-scale to this ceiling.</summary>
    public const int MaxWidth = 1440;

    /// <summary>Reasonable quality/size tradeoff for social media JPEGs.</summary>
    private const int JpegQuality = 85;

    private readonly ILogger<InstagramDerivativeService> _logger;

    public InstagramDerivativeService(ILogger<InstagramDerivativeService> logger)
    {
        _logger = logger;
    }

    public bool ShouldGenerateForContentType(string? contentType)
    {
        // Only PNG originals get a derivative. JPEG is already Instagram-safe; WebP is
        // unsupported for Instagram in this phase and must NOT be silently converted.
        return string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<InstagramDerivativeResult> GenerateAsync(Stream pngSource, CancellationToken cancellationToken = default)
    {
        // Decode the PNG. Load<Rgba32> keeps the alpha channel so we can flatten it.
        using var image = await Image.LoadAsync<Rgba32>(pngSource, cancellationToken);

        // Confirm the bytes really are PNG; never convert a mislabeled WebP/JPEG.
        var format = image.Metadata.DecodedImageFormat;
        if (format is not PngFormat)
        {
            throw new InvalidOperationException(
                $"Instagram derivative source is not PNG (decoded as {format?.Name ?? "unknown"}).");
        }

        // Downscale to <= MaxWidth, preserving aspect ratio. Never upscale.
        if (image.Width > MaxWidth)
        {
            var targetHeight = (int)Math.Round((double)image.Height * MaxWidth / image.Width);
            targetHeight = Math.Max(1, targetHeight);
            image.Mutate(ctx => ctx.Resize(MaxWidth, targetHeight));
        }

        // Flatten any transparency onto white. JPEG has no alpha channel; without this,
        // transparent regions encode as black. White is the safe, expected background.
        image.Mutate(ctx => ctx.BackgroundColor(Color.White));

        var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = JpegQuality }, cancellationToken);
        output.Position = 0;

        _logger.LogInformation(
            "Generated Instagram JPEG derivative: {Width}x{Height} {SizeBytes} bytes",
            image.Width, image.Height, output.Length);

        return new InstagramDerivativeResult(
            JpegBytes: output,
            Width: image.Width,
            Height: image.Height,
            SizeBytes: output.Length,
            MimeType: "image/jpeg");
    }

    /// <summary>
    /// Deterministic derivative key in the SAME media folder as the original. The original
    /// key shape is left untouched (no migration); we only append a sibling object.
    /// e.g. "users/.../media/{mediaId}/holiday.png" -> "users/.../media/{mediaId}/holiday.jpg".
    /// The original file base name is kept; only the extension changes to ".jpg".
    /// </summary>
    public string BuildDerivativeKey(string originalStorageKey)
    {
        var lastSlash = originalStorageKey.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? originalStorageKey[(lastSlash + 1)..] : originalStorageKey;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "instagram";

        var derivativeFileName = $"{baseName}.jpg";

        if (lastSlash >= 0)
        {
            var folder = originalStorageKey[..lastSlash];
            return $"{folder}/{derivativeFileName}";
        }

        return derivativeFileName;
    }
}
