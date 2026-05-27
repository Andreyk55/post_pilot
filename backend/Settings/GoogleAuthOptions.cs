namespace PostPilot.Api.Settings;

/// <summary>
/// Google OAuth client credentials. Bound from the "GoogleAuth" section.
/// Both values come from the Google Cloud Console for the OAuth 2.0 client.
/// </summary>
public class GoogleAuthOptions
{
    public const string SectionName = "GoogleAuth";

    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
