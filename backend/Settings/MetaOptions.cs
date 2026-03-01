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
}
