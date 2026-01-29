namespace PostPilot.Api.DTOs;

/// <summary>
/// Available AI text actions.
/// </summary>
public enum AiTextAction
{
    Polish,
    RewriteTone,
    Shorten,
    Expand,
    Hashtags,
    PreFlight
}

/// <summary>
/// Supported tones for rewriting text.
/// </summary>
public enum AiTone
{
    Professional,
    Casual,
    Funny,
    Sales
}

/// <summary>
/// Supported social media platforms for AI text optimization.
/// </summary>
public enum AiPlatform
{
    Facebook,
    Instagram,
    LinkedIn,
    X
}

/// <summary>
/// Request for AI text assistance.
/// </summary>
public record AiTextRequest(
    AiTextAction Action,
    AiPlatform Platform,
    string Text,
    AiTone? Tone = null,
    string Language = "en"
);

/// <summary>
/// A single text variant suggestion.
/// </summary>
public record AiTextVariant(
    string Title,
    string Text
);

/// <summary>
/// Response containing text variants (for Polish, RewriteTone, Shorten, Expand actions).
/// </summary>
public record AiTextVariantsResponse(
    AiTextAction Action,
    List<AiTextVariant> Variants
);

/// <summary>
/// Response containing hashtag suggestions.
/// </summary>
public record AiHashtagsResponse(
    AiTextAction Action,
    List<string> Hashtags
);

/// <summary>
/// Severity level for pre-flight issues.
/// </summary>
public enum AiIssueSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// A single pre-flight check issue.
/// </summary>
public record AiPreFlightIssue(
    AiIssueSeverity Severity,
    string Message,
    string? SuggestedFix
);

/// <summary>
/// Response containing pre-flight check results.
/// </summary>
public record AiPreFlightResponse(
    AiTextAction Action,
    int Score,
    List<AiPreFlightIssue> Issues
);
