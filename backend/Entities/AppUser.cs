namespace PostPilot.Api.Entities;

/// <summary>
/// Real end-user identity, distinct from the temporary global password gate.
/// One AppUser per (AuthProvider, ExternalAuthUserId) pair — so the same
/// person logging in with Google and (later) Microsoft would produce two rows
/// unless we add an account-linking step.
/// </summary>
public class AppUser
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>
    /// External identity provider name, e.g. "google". String column rather
    /// than enum so adding a new provider does not require a migration.
    /// </summary>
    public required string AuthProvider { get; set; }

    /// <summary>
    /// Stable, opaque user id from the external provider (Google's "sub"
    /// claim for Google). Never an email — emails change, sub does not.
    /// </summary>
    public required string ExternalAuthUserId { get; set; }

    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
