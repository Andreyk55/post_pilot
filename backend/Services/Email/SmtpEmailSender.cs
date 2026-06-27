using System.Net;
using System.Net.Mail;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Email;

/// <summary>
/// <see cref="IEmailSender"/> backed by <see cref="System.Net.Mail.SmtpClient"/>. Chosen
/// over a third-party package so the project gains email with no new dependency; the
/// <see cref="IEmailSender"/> seam means we can move to a hosted provider later without
/// touching callers.
///
/// Credentials come from <see cref="SmtpOptions"/> (backend config/env only). Anonymous
/// relays are supported by leaving the username blank. <see cref="SmtpOptions.UseTls"/>
/// maps to STARTTLS (<c>EnableSsl</c>). Exceptions propagate to the caller — the support
/// notification path catches them so a mail outage never breaks the user request.
/// </summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(SmtpOptions options, ILogger<SmtpEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        // Anonymous relay when no username is configured; otherwise authenticate.
        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        using var mail = new MailMessage(message.From, message.To)
        {
            Subject = message.Subject,
            Body = message.TextBody,
            IsBodyHtml = false,
        };

        // Optional HTML alternate view. The builder is responsible for escaping any
        // user-supplied content placed into HtmlBody.
        if (!string.IsNullOrEmpty(message.HtmlBody))
        {
            mail.AlternateViews.Add(
                AlternateView.CreateAlternateViewFromString(
                    message.HtmlBody, null, "text/html"));
        }

        _logger.LogDebug("Sending email via SMTP {Host}:{Port} (tls={Tls}).",
            _options.Host, _options.Port, _options.UseTls);

        await client.SendMailAsync(mail, ct);
    }
}
