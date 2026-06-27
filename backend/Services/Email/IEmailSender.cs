namespace PostPilot.Api.Services.Email;

/// <summary>
/// Minimal transport-agnostic email abstraction. The only implementation today is
/// <see cref="SmtpEmailSender"/>, but callers depend on this interface so the transport
/// (SMTP / a hosted provider) can be swapped without touching business logic.
///
/// Implementations send synchronously-from-the-caller's-view (await to completion) and
/// throw on failure — callers that must not fail (e.g. best-effort notifications) wrap
/// the call in their own try/catch.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>
/// A single outbound email. <see cref="HtmlBody"/> is optional — when null the message is
/// plain text only. Any user-supplied content placed in <see cref="HtmlBody"/> MUST be
/// HTML-escaped by the builder before it reaches here.
/// </summary>
public sealed record EmailMessage(
    string To,
    string From,
    string Subject,
    string TextBody,
    string? HtmlBody = null);
