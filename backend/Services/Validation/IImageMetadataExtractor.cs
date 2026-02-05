namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Interface for extracting metadata from image files.
/// </summary>
public interface IImageMetadataExtractor
{
    /// <summary>
    /// Extracts metadata from an image file.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <returns>Extracted metadata or null if extraction fails.</returns>
    Task<ImageMetadata?> ExtractAsync(string filePath);

    /// <summary>
    /// Extracts metadata from an image stream.
    /// </summary>
    /// <param name="stream">Stream containing the image data.</param>
    /// <returns>Extracted metadata or null if extraction fails.</returns>
    Task<ImageMetadata?> ExtractFromStreamAsync(Stream stream);

    /// <summary>
    /// Verifies the actual MIME type of an image by reading file headers.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <returns>The actual MIME type or null if not an image.</returns>
    Task<string?> GetActualMimeTypeAsync(string filePath);
}

/// <summary>
/// Metadata extracted from an image file.
/// </summary>
public record ImageMetadata(
    int Width,
    int Height,
    string MimeType,
    string? Format // jpeg, png, gif, etc.
);
