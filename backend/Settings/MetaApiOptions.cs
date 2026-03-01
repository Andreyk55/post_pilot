namespace PostPilot.Api.Settings;

/// <summary>
/// Meta Graph API URL constants, exposed as a singleton for DI.
/// Defaults are code constants — not in appsettings.
/// </summary>
public class MetaApiOptions
{
    public string GraphApiBaseUrl { get; } = "https://graph.facebook.com/v21.0";
    public string OAuthDialogBaseUrl { get; } = "https://www.facebook.com/v21.0/dialog/oauth";
}
