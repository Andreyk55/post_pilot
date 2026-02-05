using PostPilot.Api.Enums;

namespace PostPilot.Api.DTOs;

/// <summary>
/// Result of media validation.
/// </summary>
public record MediaValidationResult(
    ValidationStatus Status,
    MediaValidationError[] Errors,
    MediaValidationWarning[] Warnings,
    ExtractedMediaMetadata? Metadata
);

/// <summary>
/// A validation error that prevents publishing.
/// </summary>
public record MediaValidationError(
    string Code,
    string Field,
    string Message,
    string? Expected,
    string? Actual
);

/// <summary>
/// A validation warning (media can still be published but may not be optimal).
/// </summary>
public record MediaValidationWarning(
    string Code,
    string Field,
    string Message,
    string? Recommendation
);

/// <summary>
/// Metadata extracted from a media file.
/// </summary>
public record ExtractedMediaMetadata(
    int? Width,
    int? Height,
    double? DurationSeconds,
    double? AspectRatio,
    string? MimeType,
    long? SizeBytes,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    double? Fps
);

/// <summary>
/// Validation error codes.
/// </summary>
public static class MediaValidationErrorCodes
{
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string UnsupportedMimeType = "UNSUPPORTED_MIME_TYPE";
    public const string DimensionsTooSmall = "DIMENSIONS_TOO_SMALL";
    public const string DimensionsTooLarge = "DIMENSIONS_TOO_LARGE";
    public const string AspectRatioInvalid = "ASPECT_RATIO_INVALID";
    public const string DurationTooShort = "DURATION_TOO_SHORT";
    public const string DurationTooLong = "DURATION_TOO_LONG";
    public const string FpsTooLow = "FPS_TOO_LOW";
    public const string FpsTooHigh = "FPS_TOO_HIGH";
    public const string UnsupportedContainer = "UNSUPPORTED_CONTAINER";
    public const string UnsupportedVideoCodec = "UNSUPPORTED_VIDEO_CODEC";
    public const string UnsupportedAudioCodec = "UNSUPPORTED_AUDIO_CODEC";
    public const string MetadataExtractionFailed = "METADATA_EXTRACTION_FAILED";
    public const string NoRulesForCombination = "NO_RULES_FOR_COMBINATION";
}

/// <summary>
/// Validation warning codes.
/// </summary>
public static class MediaValidationWarningCodes
{
    public const string DimensionsBelowRecommended = "DIMENSIONS_BELOW_RECOMMENDED";
    public const string AspectRatioSuboptimal = "ASPECT_RATIO_SUBOPTIMAL";
}

/// <summary>
/// DTO for validation rules (returned by API for frontend pre-validation).
/// </summary>
public record MediaValidationRuleDto(
    string[] AllowedMimeTypes,
    long MaxBytes,
    int MinWidth,
    int MinHeight,
    int MaxWidth,
    int MaxHeight,
    double AspectRatioMin,
    double AspectRatioMax,
    double? DurationMinSeconds,
    double? DurationMaxSeconds,
    int? RecommendedWidth,
    int? RecommendedHeight
);
