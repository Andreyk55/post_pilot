namespace PostPilot.Api.Settings;

/// <summary>
/// Real-user auth / session cookie configuration. Distinct from the temporary
/// global password gate (<see cref="PrivateAccessOptions"/>). Bound from the
/// "Auth" config section.
/// </summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Frontend origin that the backend redirects back to after a successful
    /// Google sign-in (e.g. "https://app.example.com" or "http://localhost:5173").
    /// Trailing slash optional.
    /// </summary>
    public string FrontendUrl { get; set; } = string.Empty;

    /// <summary>
    /// Origins permitted by CORS *and* permitted as `returnUrl` targets.
    /// Localhost dev origins are always allowed in addition to this list.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Optional cookie Domain attribute. Set when the API and frontend live
    /// under the same parent domain (e.g. ".example.com" so the cookie is
    /// shared between api.example.com and app.example.com). Leave empty for
    /// cross-site setups where the cookie should be bound to the API origin
    /// only.
    /// </summary>
    public string? CookieDomain { get; set; }

    /// <summary>
    /// Forces Secure + SameSite=None on the session cookie. Defaults to true
    /// because the production deployment is HTTPS. Set false only for local
    /// development on http://localhost.
    /// </summary>
    public bool RequireHttpsCookies { get; set; } = true;

    /// <summary>
    /// Name of the session cookie issued after a successful Google login.
    /// Intentionally distinct from the private-access cookie name so the two
    /// gates remain independent.
    /// </summary>
    public string CookieName { get; set; } = "postpilot_session";
}
