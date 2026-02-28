namespace PostPilot.Api.Settings;

/// <summary>
/// Meta (Facebook/Instagram) OAuth configuration.
/// Bound from "Meta" config section.
///
/// Canonical env vars (preferred):
///   Meta__AppId, Meta__AppSecret, Meta__RedirectUri
/// Legacy env vars (deprecated, compat only):
///   META_APP_ID, META_APP_SECRET
/// </summary>
public class MetaOptions
{
    public const string SectionName = "Meta";

    /// <summary>Facebook/Instagram OAuth App ID (secret — env var only).</summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>Facebook/Instagram OAuth App Secret (secret — env var only).</summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>OAuth redirect URI. Set via config or Meta__RedirectUri env var.</summary>
    public string RedirectUri { get; set; } = string.Empty;
}
