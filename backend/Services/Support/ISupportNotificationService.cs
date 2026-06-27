using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Support;

/// <summary>
/// Builds and sends the internal "new support request" email notification. Separated from
/// <see cref="ISupportContactService"/> (which owns DB persistence) so the email concern is
/// isolated and easily faked in tests.
///
/// The notification is best-effort: callers check <see cref="IsEnabled"/> and wrap the send
/// in their own try/catch. This service throws on a real send failure (so the caller can
/// record it) but never leaks SMTP secrets in the thrown message.
/// </summary>
public interface ISupportNotificationService
{
    /// <summary>True when notifications are configured/enabled. When false, callers skip sending.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Sends the notification for <paramref name="request"/> to the configured internal
    /// support inbox. Throws if the underlying transport fails.
    /// </summary>
    Task SendSupportRequestNotificationAsync(
        SupportContactRequest request,
        AppUser user,
        CancellationToken ct);
}
