namespace PostPilot.Api.DTOs;

/// <summary>
/// Request to create a new voice profile.
/// </summary>
public record CreateVoiceProfileRequest(
    string Name,
    string? Description = null,
    string? DoRules = null,
    string? DontRules = null,
    string? BannedWords = null,
    string? ExamplePosts = null
);

/// <summary>
/// Request to update an existing voice profile.
/// </summary>
public record UpdateVoiceProfileRequest(
    string Name,
    string? Description = null,
    string? DoRules = null,
    string? DontRules = null,
    string? BannedWords = null,
    string? ExamplePosts = null
);

/// <summary>
/// Voice profile response returned to clients.
/// </summary>
public record VoiceProfileResponse(
    Guid Id,
    string Name,
    string? Description,
    string? DoRules,
    string? DontRules,
    string? BannedWords,
    string? ExamplePosts,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>
/// Summary response for voice profile list (lightweight).
/// </summary>
public record VoiceProfileSummary(
    Guid Id,
    string Name,
    DateTime UpdatedAt
);
