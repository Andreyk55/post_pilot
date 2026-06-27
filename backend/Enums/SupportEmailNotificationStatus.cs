namespace PostPilot.Api.Enums;

/// <summary>
/// Delivery state of the internal email NOTIFICATION sent when a
/// <see cref="Entities.SupportContactRequest"/> is created. The DB row is the source of
/// truth for the support request itself; this only tracks whether we managed to ping the
/// support inbox. Email is best-effort — a <see cref="Failed"/> notification never means
/// the user's message was lost.
/// </summary>
public enum SupportEmailNotificationStatus
{
    /// <summary>No send was attempted (notifications disabled, or user could not be loaded).</summary>
    NotAttempted = 0,

    /// <summary>The notification email was handed off to the mail transport successfully.</summary>
    Sent = 1,

    /// <summary>The send was attempted but failed. A short, safe error is stored alongside.</summary>
    Failed = 2,
}
