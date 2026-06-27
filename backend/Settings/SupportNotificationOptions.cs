namespace PostPilot.Api.Settings;

/// <summary>
/// Internal support-notification settings. Bound from the "Support" config section.
///
/// The destination <see cref="NotificationEmail"/> is BACKEND-ONLY — it is never sent to
/// the frontend, returned in an API response, or shown in any UI. When
/// <see cref="Enabled"/> is false the support request is still saved to the DB; only the
/// email step is skipped.
/// </summary>
public class SupportNotificationOptions
{
    public const string SectionName = "Support";

    /// <summary>
    /// Master switch for the email notification. Off by default so local/dev environments
    /// (and tests) need no SMTP configured. When true, the validator requires the
    /// destination/from addresses and an SMTP host at startup.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Internal inbox that receives the "new support request" notification.</summary>
    public string NotificationEmail { get; set; } = string.Empty;

    /// <summary>From-address used for the notification (e.g. noreply@yourdomain.com).</summary>
    public string FromEmail { get; set; } = string.Empty;
}
