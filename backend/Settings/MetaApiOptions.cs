namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for Meta (Facebook/Instagram) Graph API.
/// Bound from "Meta:Api" config section.
/// </summary>
public class MetaApiOptions
{
    public const string SectionName = "Meta:Api";

    /// <summary>
    /// Base URL for Meta Graph API requests.
    /// </summary>
    public string GraphApiBaseUrl { get; set; } = null!;

    /// <summary>
    /// Base URL for Meta OAuth dialog.
    /// </summary>
    public string OAuthDialogBaseUrl { get; set; } = null!;
}
