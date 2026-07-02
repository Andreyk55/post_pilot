namespace PostPilot.Api.Settings;

/// <summary>
/// Public frontend configuration. Bound from the "Frontend" config section
/// (env: <c>Frontend__BaseUrl</c>).
/// </summary>
public class FrontendOptions
{
    public const string SectionName = "Frontend";

    /// <summary>
    /// Public origin of the frontend, e.g. "https://www.publishharbor.com".
    /// Required wherever the backend must emit an absolute frontend URL
    /// (e.g. the Meta data-deletion status page). No hardcoded fallback:
    /// endpoints that need it fail with a server error when it is missing.
    /// Trailing slash optional.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;
}
