namespace PostPilot.Api.Settings;

/// <summary>
/// Meta (Facebook/Instagram) OAuth configuration. Bound from "Meta" config section.
/// AppId/AppSecret: required env vars. RedirectUri: from appsettings.
/// </summary>
public class MetaOptions
{
    public const string SectionName = "Meta";

    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// Enables the Meta diagnostic endpoints (e.g. GET /api/meta/instagram/debug).
    /// OFF by default and OFF in production unless explicitly set. When false the endpoints
    /// return 404 outside the Development environment. Set via Meta__EnableDebugEndpoints.
    /// These endpoints are for troubleshooting only and must never be exposed publicly.
    /// </summary>
    public bool EnableDebugEndpoints { get; set; } = false;
}
