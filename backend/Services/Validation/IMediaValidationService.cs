using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Service for validating media files against platform-specific rules.
/// All operations are stateless - no database persistence.
/// </summary>
public interface IMediaValidationService
{
    /// <summary>
    /// Validates a media file against rules for the specified platform and placement.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="mimeType">MIME type of the file.</param>
    /// <param name="sizeBytes">Size of the file in bytes.</param>
    /// <param name="mediaType">Type of media (Image or Video).</param>
    /// <param name="platform">Target platform for validation.</param>
    /// <param name="placement">Target placement for validation.</param>
    /// <returns>Validation result with status, errors, and warnings.</returns>
    Task<MediaValidationResult> ValidateFileAsync(
        string filePath,
        string mimeType,
        long sizeBytes,
        MediaType mediaType,
        Platform platform,
        Placement placement);

    /// <summary>
    /// Extracts metadata from a media file without validation.
    /// </summary>
    /// <param name="filePath">Path to the media file.</param>
    /// <param name="mediaType">Type of media (Image or Video).</param>
    /// <returns>Extracted metadata, or null if extraction fails.</returns>
    Task<ExtractedMediaMetadata?> ExtractMetadataFromFileAsync(string filePath, MediaType mediaType);
}
