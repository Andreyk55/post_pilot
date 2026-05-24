namespace PostPilot.Api.Settings;

/// <summary>
/// Temporary single-password access gate. Bound from the "PrivateAccess"
/// config section. Intended to be enabled in production while the app is
/// private and removed once real auth/onboarding lands.
/// </summary>
public class PrivateAccessOptions
{
    public const string SectionName = "PrivateAccess";

    /// <summary>
    /// Master switch. When false, the middleware lets every request through
    /// and /api/private-access/me always returns hasAccess=true. Should be
    /// false in dev/local and true in production while the app is private.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// BCrypt hash of the shared password. Generated via the helper described
    /// in the README — never store the plain password here.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Name of the cookie set on successful login. The value is an opaque
    /// HMAC-signed token; the cookie itself carries no user identity.
    /// </summary>
    public string CookieName { get; set; } = "postpilot_private_access";

    /// <summary>
    /// How long a successful login lasts (cookie + token expiration).
    /// </summary>
    public int SessionDays { get; set; } = 7;

    /// <summary>
    /// Secret used to HMAC-sign the cookie value so a captured value cannot
    /// be forged. Optional — when empty, a value derived from PasswordHash
    /// is used. Set this explicitly (32+ random chars) in production so that
    /// rotating the password also invalidates outstanding cookies cleanly.
    /// </summary>
    public string? CookieSigningKey { get; set; }
}
