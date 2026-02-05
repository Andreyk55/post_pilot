using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Implementation of media validation service.
/// Validates media files against platform-specific rules and extracts metadata.
/// All operations are stateless - no database persistence.
/// </summary>
public class MediaValidationService : IMediaValidationService
{
    private readonly IImageMetadataExtractor _imageExtractor;
    private readonly IVideoMetadataExtractor _videoExtractor;
    private readonly ILogger<MediaValidationService> _logger;

    public MediaValidationService(
        IImageMetadataExtractor imageExtractor,
        IVideoMetadataExtractor videoExtractor,
        ILogger<MediaValidationService> logger)
    {
        _imageExtractor = imageExtractor;
        _videoExtractor = videoExtractor;
        _logger = logger;
    }

    public async Task<MediaValidationResult> ValidateFileAsync(
        string filePath,
        string mimeType,
        long sizeBytes,
        MediaType mediaType,
        Platform platform,
        Placement placement)
    {
        var errors = new List<MediaValidationError>();
        var warnings = new List<MediaValidationWarning>();
        ExtractedMediaMetadata? metadata = null;

        // Get validation rules
        var rules = MediaValidationRules.GetRules(platform, placement, mediaType);
        if (rules == null)
        {
            errors.Add(new MediaValidationError(
                MediaValidationErrorCodes.NoRulesForCombination,
                "combination",
                $"No validation rules defined for {platform}/{placement}/{mediaType}",
                null,
                $"{platform}/{placement}/{mediaType}"));

            return new MediaValidationResult(
                ValidationStatus.Invalid,
                errors.ToArray(),
                warnings.ToArray(),
                null);
        }

        // Extract metadata based on media type
        if (mediaType == MediaType.Image)
        {
            var imageMetadata = await _imageExtractor.ExtractAsync(filePath);
            if (imageMetadata != null)
            {
                var aspectRatio = imageMetadata.Height > 0
                    ? (double)imageMetadata.Width / imageMetadata.Height
                    : 0;

                metadata = new ExtractedMediaMetadata(
                    Width: imageMetadata.Width,
                    Height: imageMetadata.Height,
                    DurationSeconds: null,
                    AspectRatio: Math.Round(aspectRatio, 4),
                    MimeType: imageMetadata.MimeType,
                    SizeBytes: sizeBytes,
                    Container: null,
                    VideoCodec: null,
                    AudioCodec: null,
                    Fps: null);

                // Verify actual MIME type matches
                var actualMime = imageMetadata.MimeType;
                if (!string.Equals(actualMime, mimeType, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "MIME type mismatch: declared {Declared}, actual {Actual}",
                        mimeType, actualMime);
                    mimeType = actualMime; // Use actual MIME type for validation
                }
            }
            else
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.MetadataExtractionFailed,
                    "metadata",
                    "Failed to extract image metadata. The file may be corrupted or not a valid image.",
                    null, null));
            }
        }
        else if (mediaType == MediaType.Video)
        {
            _logger.LogInformation("Extracting video metadata for file: {FilePath}", filePath);
            var videoMetadata = await _videoExtractor.ExtractAsync(filePath);
            if (videoMetadata != null)
            {
                _logger.LogInformation(
                    "Video metadata extracted: {Width}x{Height}, Duration={Duration}s, FPS={Fps}, Codec={VideoCodec}, Container={Container}",
                    videoMetadata.Width, videoMetadata.Height, videoMetadata.DurationSeconds,
                    videoMetadata.Fps, videoMetadata.VideoCodec, videoMetadata.Container);

                var aspectRatio = videoMetadata.Height > 0
                    ? (double)videoMetadata.Width / videoMetadata.Height
                    : 0;

                metadata = new ExtractedMediaMetadata(
                    Width: videoMetadata.Width,
                    Height: videoMetadata.Height,
                    DurationSeconds: videoMetadata.DurationSeconds,
                    AspectRatio: Math.Round(aspectRatio, 4),
                    MimeType: videoMetadata.MimeType,
                    SizeBytes: sizeBytes,
                    Container: videoMetadata.Container,
                    VideoCodec: videoMetadata.VideoCodec,
                    AudioCodec: videoMetadata.AudioCodec,
                    Fps: videoMetadata.Fps);
            }
            else
            {
                _logger.LogWarning("Failed to extract video metadata for file: {FilePath}", filePath);
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.MetadataExtractionFailed,
                    "metadata",
                    "Failed to extract video metadata. Ensure ffprobe is installed and the file is a valid video.",
                    null, null));
            }
        }

        // If metadata extraction failed, we can only validate size and MIME type
        if (metadata == null && errors.Count > 0)
        {
            return new MediaValidationResult(
                ValidationStatus.Invalid,
                errors.ToArray(),
                warnings.ToArray(),
                null);
        }

        // Validate against rules
        ValidateRules(rules, mimeType, sizeBytes, metadata, errors, warnings);

        // Determine final status
        var status = errors.Count > 0
            ? ValidationStatus.Invalid
            : warnings.Count > 0
                ? ValidationStatus.Warning
                : ValidationStatus.Valid;

        if (status == ValidationStatus.Valid)
        {
            _logger.LogInformation(
                "{MediaType} validation PASSED for {Platform}/{Placement}",
                mediaType, platform, placement);
        }
        else if (status == ValidationStatus.Warning)
        {
            _logger.LogInformation(
                "{MediaType} validation PASSED WITH WARNINGS for {Platform}/{Placement}: {Warnings}",
                mediaType, platform, placement,
                string.Join(", ", warnings.Select(w => w.Message)));
        }
        else
        {
            _logger.LogWarning(
                "{MediaType} validation FAILED for {Platform}/{Placement}: {Errors}",
                mediaType, platform, placement,
                string.Join(", ", errors.Select(e => e.Message)));
        }

        return new MediaValidationResult(status, errors.ToArray(), warnings.ToArray(), metadata);
    }

    public async Task<ExtractedMediaMetadata?> ExtractMetadataFromFileAsync(string filePath, MediaType mediaType)
    {
        if (!File.Exists(filePath))
            return null;

        var fileInfo = new FileInfo(filePath);
        var sizeBytes = fileInfo.Length;

        if (mediaType == MediaType.Image)
        {
            var imageMetadata = await _imageExtractor.ExtractAsync(filePath);
            if (imageMetadata != null)
            {
                var aspectRatio = imageMetadata.Height > 0
                    ? (double)imageMetadata.Width / imageMetadata.Height
                    : 0;

                return new ExtractedMediaMetadata(
                    Width: imageMetadata.Width,
                    Height: imageMetadata.Height,
                    DurationSeconds: null,
                    AspectRatio: Math.Round(aspectRatio, 4),
                    MimeType: imageMetadata.MimeType,
                    SizeBytes: sizeBytes,
                    Container: null,
                    VideoCodec: null,
                    AudioCodec: null,
                    Fps: null);
            }
        }
        else if (mediaType == MediaType.Video)
        {
            var videoMetadata = await _videoExtractor.ExtractAsync(filePath);
            if (videoMetadata != null)
            {
                var aspectRatio = videoMetadata.Height > 0
                    ? (double)videoMetadata.Width / videoMetadata.Height
                    : 0;

                return new ExtractedMediaMetadata(
                    Width: videoMetadata.Width,
                    Height: videoMetadata.Height,
                    DurationSeconds: videoMetadata.DurationSeconds,
                    AspectRatio: Math.Round(aspectRatio, 4),
                    MimeType: videoMetadata.MimeType,
                    SizeBytes: sizeBytes,
                    Container: videoMetadata.Container,
                    VideoCodec: videoMetadata.VideoCodec,
                    AudioCodec: videoMetadata.AudioCodec,
                    Fps: videoMetadata.Fps);
            }
        }

        return null;
    }

    private void ValidateRules(
        MediaValidationRule rules,
        string mimeType,
        long sizeBytes,
        ExtractedMediaMetadata? metadata,
        List<MediaValidationError> errors,
        List<MediaValidationWarning> warnings)
    {
        // 1. Validate MIME type
        if (!rules.AllowedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(new MediaValidationError(
                MediaValidationErrorCodes.UnsupportedMimeType,
                "mimeType",
                $"File type '{mimeType}' is not supported. Allowed types: {string.Join(", ", rules.AllowedMimeTypes)}",
                string.Join(", ", rules.AllowedMimeTypes),
                mimeType));
        }

        // 2. Validate file size
        if (sizeBytes > rules.MaxBytes)
        {
            var maxMB = rules.MaxBytes / (1024.0 * 1024.0);
            var actualMB = sizeBytes / (1024.0 * 1024.0);
            errors.Add(new MediaValidationError(
                MediaValidationErrorCodes.FileTooLarge,
                "sizeBytes",
                $"File size ({actualMB:F1}MB) exceeds maximum allowed ({maxMB:F1}MB)",
                $"{maxMB:F1}MB",
                $"{actualMB:F1}MB"));
        }

        if (metadata == null)
            return;

        // 3. Validate dimensions
        if (metadata.Width.HasValue && metadata.Height.HasValue)
        {
            var width = metadata.Width.Value;
            var height = metadata.Height.Value;

            if (width < rules.MinWidth || height < rules.MinHeight)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DimensionsTooSmall,
                    "dimensions",
                    $"Dimensions ({width}x{height}) are too small. Minimum: {rules.MinWidth}x{rules.MinHeight}",
                    $"{rules.MinWidth}x{rules.MinHeight}",
                    $"{width}x{height}"));
            }

            if (width > rules.MaxWidth || height > rules.MaxHeight)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DimensionsTooLarge,
                    "dimensions",
                    $"Dimensions ({width}x{height}) are too large. Maximum: {rules.MaxWidth}x{rules.MaxHeight}",
                    $"{rules.MaxWidth}x{rules.MaxHeight}",
                    $"{width}x{height}"));
            }

            // Check recommended dimensions (warning only)
            if (rules.RecommendedWidth.HasValue && rules.RecommendedHeight.HasValue)
            {
                if (width < rules.RecommendedWidth.Value || height < rules.RecommendedHeight.Value)
                {
                    warnings.Add(new MediaValidationWarning(
                        MediaValidationWarningCodes.DimensionsBelowRecommended,
                        "dimensions",
                        $"Dimensions ({width}x{height}) are below recommended ({rules.RecommendedWidth}x{rules.RecommendedHeight}). Quality may be reduced.",
                        $"Use at least {rules.RecommendedWidth}x{rules.RecommendedHeight} for best quality"));
                }
            }
        }

        // 4. Validate aspect ratio
        if (metadata.AspectRatio.HasValue)
        {
            var aspectRatio = metadata.AspectRatio.Value;

            if (aspectRatio < rules.AspectRatioMin || aspectRatio > rules.AspectRatioMax)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.AspectRatioInvalid,
                    "aspectRatio",
                    $"Aspect ratio ({aspectRatio:F2}) is outside allowed range ({rules.AspectRatioMin:F2} to {rules.AspectRatioMax:F2})",
                    $"{rules.AspectRatioMin:F2} to {rules.AspectRatioMax:F2}",
                    $"{aspectRatio:F2}"));
            }
        }

        // 5. Video-specific validations
        if (metadata.DurationSeconds.HasValue)
        {
            var duration = metadata.DurationSeconds.Value;

            if (rules.DurationMinSeconds.HasValue && duration < rules.DurationMinSeconds.Value)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DurationTooShort,
                    "durationSeconds",
                    $"Video duration ({duration:F1}s) is shorter than minimum ({rules.DurationMinSeconds.Value}s)",
                    $"{rules.DurationMinSeconds.Value}s",
                    $"{duration:F1}s"));
            }

            if (rules.DurationMaxSeconds.HasValue && duration > rules.DurationMaxSeconds.Value)
            {
                var maxMinutes = rules.DurationMaxSeconds.Value / 60.0;
                var actualMinutes = duration / 60.0;
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DurationTooLong,
                    "durationSeconds",
                    $"Video duration ({actualMinutes:F1} min) exceeds maximum ({maxMinutes:F1} min)",
                    $"{maxMinutes:F1} min",
                    $"{actualMinutes:F1} min"));
            }
        }

        // 6. Validate FPS (video only)
        if (metadata.Fps.HasValue)
        {
            var fps = metadata.Fps.Value;

            if (rules.MinFps.HasValue && fps < rules.MinFps.Value)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.FpsTooLow,
                    "fps",
                    $"Frame rate ({fps:F1} fps) is below minimum ({rules.MinFps.Value} fps)",
                    $"{rules.MinFps.Value} fps",
                    $"{fps:F1} fps"));
            }

            if (rules.MaxFps.HasValue && fps > rules.MaxFps.Value)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.FpsTooHigh,
                    "fps",
                    $"Frame rate ({fps:F1} fps) exceeds maximum ({rules.MaxFps.Value} fps)",
                    $"{rules.MaxFps.Value} fps",
                    $"{fps:F1} fps"));
            }
        }

        // 7. Validate container (video only)
        if (!string.IsNullOrEmpty(metadata.Container) && rules.AllowedContainers != null)
        {
            if (!rules.AllowedContainers.Contains(metadata.Container, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.UnsupportedContainer,
                    "container",
                    $"Container format '{metadata.Container}' is not supported. Allowed: {string.Join(", ", rules.AllowedContainers)}",
                    string.Join(", ", rules.AllowedContainers),
                    metadata.Container));
            }
        }

        // 8. Validate video codec
        if (!string.IsNullOrEmpty(metadata.VideoCodec) && rules.AllowedVideoCodecs != null)
        {
            if (!rules.AllowedVideoCodecs.Contains(metadata.VideoCodec, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.UnsupportedVideoCodec,
                    "videoCodec",
                    $"Video codec '{metadata.VideoCodec}' is not supported. Allowed: {string.Join(", ", rules.AllowedVideoCodecs)}",
                    string.Join(", ", rules.AllowedVideoCodecs),
                    metadata.VideoCodec));
            }
        }

        // 9. Validate audio codec
        if (!string.IsNullOrEmpty(metadata.AudioCodec) && rules.AllowedAudioCodecs != null)
        {
            if (!rules.AllowedAudioCodecs.Contains(metadata.AudioCodec, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.UnsupportedAudioCodec,
                    "audioCodec",
                    $"Audio codec '{metadata.AudioCodec}' is not supported. Allowed: {string.Join(", ", rules.AllowedAudioCodecs)}",
                    string.Join(", ", rules.AllowedAudioCodecs),
                    metadata.AudioCodec));
            }
        }
    }
}
