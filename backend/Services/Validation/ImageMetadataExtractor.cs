using SixLabors.ImageSharp;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Extracts metadata from image files using ImageSharp.
/// </summary>
public class ImageMetadataExtractor : IImageMetadataExtractor
{
    private readonly ILogger<ImageMetadataExtractor> _logger;

    public ImageMetadataExtractor(ILogger<ImageMetadataExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<ImageMetadata?> ExtractAsync(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return await ExtractFromStreamAsync(stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract image metadata from {FilePath}", filePath);
            return null;
        }
    }

    public async Task<ImageMetadata?> ExtractFromStreamAsync(Stream stream)
    {
        try
        {
            // ImageSharp can identify images without fully decoding them
            var info = await Image.IdentifyAsync(stream);
            if (info == null)
            {
                return null;
            }

            var mimeType = GetMimeTypeFromFormat(info.Metadata.DecodedImageFormat?.Name);

            return new ImageMetadata(
                Width: info.Width,
                Height: info.Height,
                MimeType: mimeType ?? "application/octet-stream",
                Format: info.Metadata.DecodedImageFormat?.Name?.ToLowerInvariant()
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract image metadata from stream");
            return null;
        }
    }

    public async Task<string?> GetActualMimeTypeAsync(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var info = await Image.IdentifyAsync(stream);
            if (info?.Metadata.DecodedImageFormat != null)
            {
                return GetMimeTypeFromFormat(info.Metadata.DecodedImageFormat.Name);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to identify image MIME type for {FilePath}", filePath);
            return null;
        }
    }

    private static string? GetMimeTypeFromFormat(string? format)
    {
        if (string.IsNullOrEmpty(format))
            return null;

        return format.ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "png" => "image/png",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "webp" => "image/webp",
            "tiff" => "image/tiff",
            _ => $"image/{format.ToLowerInvariant()}"
        };
    }
}
