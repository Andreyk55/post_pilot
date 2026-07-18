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
        Placement placement,
        bool isCarouselItem = false)
    {
        var errors = new List<MediaValidationError>();
        var warnings = new List<MediaValidationWarning>();
        ExtractedMediaMetadata? metadata = null;

        // Get validation rules (carousel items may use a stricter per-item rule, e.g. IG Feed
        // carousel video is capped at 60s vs 180s for a single Feed video).
        var rules = MediaValidationRules.GetRules(platform, placement, mediaType, isCarouselItem);
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
                    Fps: videoMetadata.Fps,
                    HasVideoStream: videoMetadata.HasVideoStream);

                if (!videoMetadata.HasVideoStream)
                {
                    errors.Add(new MediaValidationError(
                        MediaValidationErrorCodes.VideoStreamMissing,
                        "videoStream",
                        "The file does not contain a video stream. Upload a readable MP4 or MOV video.",
                        "At least one video stream",
                        "No video stream"));
                }
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
        ValidateRules(rules, mimeType, sizeBytes, metadata, errors, warnings, platform, placement, mediaType, isCarouselItem);

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
                    Fps: videoMetadata.Fps,
                    HasVideoStream: videoMetadata.HasVideoStream);
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
        List<MediaValidationWarning> warnings,
        Platform platform,
        Placement placement,
        MediaType mediaType,
        bool isCarouselItem)
    {
        // 1. Validate MIME type
        if (!rules.AllowedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase))
        {
            // HEIC/HEIF (iPhone default capture format) gets dedicated copy: it is a known
            // product limitation, not a corrupt file.
            var isHeic = mimeType.Contains("heic", StringComparison.OrdinalIgnoreCase)
                || mimeType.Contains("heif", StringComparison.OrdinalIgnoreCase);
            errors.Add(new MediaValidationError(
                MediaValidationErrorCodes.UnsupportedMimeType,
                "mimeType",
                isHeic
                    ? "HEIC is not supported yet. Please upload a JPG or PNG image."
                    : $"File type '{mimeType}' is not supported. Allowed types: {string.Join(", ", rules.AllowedMimeTypes)}",
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
                mediaType == MediaType.Video
                    ? $"This video is too large. {platform} videos can be up to {maxMB:F0}MB."
                    : $"This image is too large. {platform} images can be up to {maxMB:F0}MB. Large phone photos may need to be resized before upload.",
                $"{maxMB:F0}MB",
                $"{actualMB:F1}MB"));
        }

        if (metadata == null)
            return;

        // 3. Validate dimensions. Each bound is optional: a null Min/Max is skipped, so a rule
        // with no dimension constraints (e.g. Facebook Story) never produces a dimensions error.
        if (metadata.Width.HasValue && metadata.Height.HasValue)
        {
            var width = metadata.Width.Value;
            var height = metadata.Height.Value;

            var dimensionsTooSmall = (rules.MinWidth.HasValue && width < rules.MinWidth.Value)
                || (rules.MinHeight.HasValue && height < rules.MinHeight.Value);
            if (dimensionsTooSmall)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DimensionsTooSmall,
                    "dimensions",
                    $"Dimensions ({width}x{height}) are too small. Minimum: {rules.MinWidth}x{rules.MinHeight}",
                    $"{rules.MinWidth}x{rules.MinHeight}",
                    $"{width}x{height}"));
            }

            if (rules.MaxWidthIsAdvisory)
            {
                if (rules.MaxWidth.HasValue && width > rules.MaxWidth.Value)
                {
                    warnings.Add(new MediaValidationWarning(
                        MediaValidationWarningCodes.DimensionsAboveMaxWillDownscale,
                        "dimensions",
                        "Instagram may resize this image before publishing.",
                        "Media is publishable."));
                }
            }
            else if ((rules.MaxWidth.HasValue && width > rules.MaxWidth.Value)
                || (rules.MaxHeight.HasValue && height > rules.MaxHeight.Value))
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DimensionsTooLarge,
                    "dimensions",
                    $"Dimensions ({width}x{height}) are too large. Maximum: {rules.MaxWidth}x{rules.MaxHeight}",
                    $"{rules.MaxWidth}x{rules.MaxHeight}",
                    $"{width}x{height}"));
            }

            if (!dimensionsTooSmall
                && rules.QualityWarningMinWidth.HasValue
                && rules.QualityWarningMinHeight.HasValue
                && (width < rules.QualityWarningMinWidth.Value || height < rules.QualityWarningMinHeight.Value))
            {
                warnings.Add(new MediaValidationWarning(
                    MediaValidationWarningCodes.DimensionsBelowRecommended,
                    "dimensions",
                    "For best quality, use a higher-resolution image.",
                    "Media is publishable."));
            }
        }

        // 4. Validate aspect ratio. Only when the rule defines a range: a null Min/Max skips both
        // the hard range check AND the preferred-ratio warning, so a rule with no aspect-ratio
        // requirement (e.g. Facebook or Instagram Story) produces no aspect error or warning.
        if (metadata.AspectRatio.HasValue && rules.AspectRatioMin.HasValue && rules.AspectRatioMax.HasValue)
        {
            var aspectRatio = metadata.AspectRatio.Value;

            var hasPreferredAspectRatio = rules.PreferredAspectRatio.HasValue
                && rules.AspectRatioWarningTolerance.HasValue;

            if (aspectRatio < rules.AspectRatioMin.Value || aspectRatio > rules.AspectRatioMax.Value)
            {
                var mediaNoun = mediaType == MediaType.Video ? "videos" : "images";
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.AspectRatioInvalid,
                    "aspectRatio",
                    $"{platform} {placement} {mediaNoun} must use an aspect ratio between {FormatRatio(rules.AspectRatioMin.Value)} and {FormatRatio(rules.AspectRatioMax.Value)}.",
                    $"{FormatRatio(rules.AspectRatioMin.Value)} to {FormatRatio(rules.AspectRatioMax.Value)}",
                    $"{aspectRatio:F2}"));
            }
            else if (hasPreferredAspectRatio
                && Math.Abs(aspectRatio - rules.PreferredAspectRatio!.Value) > rules.AspectRatioWarningTolerance!.Value)
            {
                warnings.Add(new MediaValidationWarning(
                    MediaValidationWarningCodes.AspectRatioSuboptimal,
                    "aspectRatio",
                    $"{platform} {placement} media is outside the preferred aspect ratio.",
                    "Media is publishable."));
            }
        }

        // 5. Video-specific validations
        if (metadata.DurationSeconds.HasValue)
        {
            var duration = metadata.DurationSeconds.Value;

            // Same actionable range copy for too-short and too-long (the fix is identical: pick a
            // video inside the range). A carousel VIDEO item gets a distinct, carousel-specific
            // message so the 60s carousel cap reads differently from the 180s single-video cap
            // ("Videos in an Instagram Feed carousel must be between 3 and 60 seconds." vs
            // "Feed videos must be between 3 and 180 seconds.").
            string? durationRangeMessage = null;
            if (rules.DurationMinSeconds.HasValue && rules.DurationMaxSeconds.HasValue)
            {
                durationRangeMessage = isCarouselItem
                    ? $"Videos in an {platform} {placement} carousel must be between {rules.DurationMinSeconds.Value:0.##} and {rules.DurationMaxSeconds.Value:0.##} seconds."
                    : $"{placement} videos must be between {rules.DurationMinSeconds.Value:0.##} and {rules.DurationMaxSeconds.Value:0.##} seconds.";
            }

            if (rules.DurationMinSeconds.HasValue && duration < rules.DurationMinSeconds.Value)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DurationTooShort,
                    "durationSeconds",
                    durationRangeMessage
                        ?? $"Video duration ({duration:F1}s) is shorter than minimum ({rules.DurationMinSeconds.Value}s)",
                    $"{rules.DurationMinSeconds.Value:0.##}s",
                    $"{duration:F1}s"));
            }

            if (rules.DurationMaxSeconds.HasValue && duration > rules.DurationMaxSeconds.Value)
            {
                errors.Add(new MediaValidationError(
                    MediaValidationErrorCodes.DurationTooLong,
                    "durationSeconds",
                    durationRangeMessage
                        ?? $"Video duration ({duration:F1}s) exceeds maximum ({rules.DurationMaxSeconds.Value}s)",
                    $"{rules.DurationMaxSeconds.Value:0.##}s",
                    $"{duration:F1}s"));
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

        // 8. Validate video codec — only when the rule defines a NON-EMPTY allow-list. A null/empty
        // list means "no codec restriction" (e.g. Facebook Story, which lets Meta decide), so no
        // UNSUPPORTED_VIDEO_CODEC is produced for it.
        if (!string.IsNullOrEmpty(metadata.VideoCodec) && rules.AllowedVideoCodecs is { Length: > 0 })
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

        // 9. Validate audio codec — same rule as video: only when a non-empty allow-list exists.
        // A null/empty list means "no restriction" (Facebook Story), so audio may be any codec or
        // absent, and no UNSUPPORTED_AUDIO_CODEC is produced.
        if (!string.IsNullOrEmpty(metadata.AudioCodec) && rules.AllowedAudioCodecs is { Length: > 0 })
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

    /// <summary>
    /// Renders a width/height ratio bound as the familiar social-media label
    /// (0.8 → "4:5", 0.5625 → "9:16") instead of a bare decimal, falling back to
    /// "{x:F2}:1" for bounds that have no common name.
    /// </summary>
    private static string FormatRatio(double ratio) => ratio switch
    {
        0.5625 => "9:16",
        0.75 => "3:4",
        0.8 => "4:5",
        1.0 => "1:1",
        _ => $"{ratio:F2}:1",
    };
}
