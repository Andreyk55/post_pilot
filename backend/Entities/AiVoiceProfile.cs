namespace PostPilot.Api.Entities;

/// <summary>
/// Represents a user's brand voice profile for AI text generation.
/// Defines consistent style, rules, and examples that guide AI output.
/// </summary>
public class AiVoiceProfile
{
    public Guid Id { get; set; }

    /// <summary>
    /// Owner of this voice profile.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Display name for this profile (e.g., "Brand Voice", "Personal", "Formal Corporate").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Description of the brand/audience this voice targets.
    /// Example: "Tech-savvy millennials interested in productivity tools"
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Rules for what the AI should do (e.g., "Use active voice", "Include statistics").
    /// Stored as newline-separated bullet points or JSON array.
    /// </summary>
    public string? DoRules { get; set; }

    /// <summary>
    /// Rules for what the AI should avoid (e.g., "Don't use jargon", "Avoid passive voice").
    /// Stored as newline-separated bullet points or JSON array.
    /// </summary>
    public string? DontRules { get; set; }

    /// <summary>
    /// Words or phrases that must never appear in output.
    /// Stored as comma-separated or JSON array.
    /// </summary>
    public string? BannedWords { get; set; }

    /// <summary>
    /// Example posts that demonstrate the desired voice/style.
    /// Multiple examples separated by blank lines.
    /// </summary>
    public string? ExamplePosts { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Soft delete fields
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
}
