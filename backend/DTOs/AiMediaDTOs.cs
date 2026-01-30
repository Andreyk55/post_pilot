namespace PostPilot.Api.DTOs;

/// <summary>
/// Available AI media actions.
/// </summary>
public enum AiMediaAction
{
    CaptionIdeas,
    ImageQualityCheck,
    AltText,
    VideoCaptionIdeas,
    ThumbnailSuggest
}

/// <summary>
/// Request for AI media assistance.
/// </summary>
public record AiMediaRequest(
    AiMediaAction Action,
    AiPlatform Platform,
    string AssetUrl,
    string AssetType,
    string? Text = null,
    string Language = "en"
);

/// <summary>
/// Base response for media AI actions.
/// </summary>
public abstract record AiMediaResponseBase(AiMediaAction Action);

/// <summary>
/// A single caption variant suggestion for media.
/// </summary>
public record AiMediaCaptionVariant(
    string Title,
    string Text
);

/// <summary>
/// Response containing caption variants for images or videos.
/// </summary>
public record AiMediaCaptionIdeasResponse(
    AiMediaAction Action,
    List<AiMediaCaptionVariant> Variants
) : AiMediaResponseBase(Action);

/// <summary>
/// A quality issue found in an image.
/// </summary>
public record AiImageQualityIssue(
    AiIssueSeverity Severity,
    string Message,
    string? SuggestedFix
);

/// <summary>
/// Response containing image quality check results.
/// </summary>
public record AiImageQualityCheckResponse(
    AiMediaAction Action,
    int Score,
    List<AiImageQualityIssue> Issues
) : AiMediaResponseBase(Action);

/// <summary>
/// Response containing generated alt text for an image.
/// </summary>
public record AiAltTextResponse(
    AiMediaAction Action,
    string AltText
) : AiMediaResponseBase(Action);

/// <summary>
/// A video frame thumbnail candidate.
/// </summary>
public record AiVideoFrame(
    double TimestampSeconds,
    string ImageUrl
);

/// <summary>
/// Response containing thumbnail suggestions for a video.
/// </summary>
public record AiThumbnailSuggestResponse(
    AiMediaAction Action,
    List<AiVideoFrame> Frames
) : AiMediaResponseBase(Action);

/// <summary>
/// A frame extracted client-side and sent to the server.
/// </summary>
public record ClientExtractedFrame(
    double TimestampSeconds,
    string ImageData // base64 data URL (data:image/jpeg;base64,...)
);

/// <summary>
/// Request containing pre-extracted frames from the client.
/// Used for thumbnail selection without server-side video processing.
/// </summary>
public record AiThumbnailFramesRequest(
    List<ClientExtractedFrame> Frames
);

/// <summary>
/// Request for video caption ideas using a pre-extracted frame.
/// Frame is extracted client-side to avoid FFmpeg dependency.
/// </summary>
public record AiVideoCaptionIdeasRequest(
    AiPlatform Platform,
    string FrameData, // base64 data URL (data:image/jpeg;base64,...)
    string? Text = null,
    string Language = "en"
);
