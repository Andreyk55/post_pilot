using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace PostPilot.Api.Settings.Validators;

/// <summary>
/// Startup validation policy for support email notifications:
///
/// <list type="bullet">
///   <item>When <see cref="SupportNotificationOptions.Enabled"/> is <c>false</c> →
///   always valid (the email step is simply skipped at runtime).</item>
///   <item>When enabled → <see cref="SupportNotificationOptions.NotificationEmail"/> and
///   <see cref="SupportNotificationOptions.FromEmail"/> must be present and well-formed,
///   and <see cref="SmtpOptions.Host"/> must be set — otherwise the host fails fast at
///   startup instead of silently never delivering.</item>
/// </list>
///
/// SMTP username/password are intentionally NOT required (anonymous relays are valid).
/// </summary>
public sealed class SupportNotificationOptionsValidator : IValidateOptions<SupportNotificationOptions>
{
    private readonly SmtpOptions _smtp;

    public SupportNotificationOptionsValidator(SmtpOptions smtp)
    {
        _smtp = smtp;
    }

    public ValidateOptionsResult Validate(string? name, SupportNotificationOptions options)
    {
        // Disabled: no requirements. The request is still saved; email is skipped.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.NotificationEmail))
            failures.Add("Support:NotificationEmail is required when Support:Enabled is true. Set via Support__NotificationEmail.");
        else if (!IsValidEmail(options.NotificationEmail))
            failures.Add("Support:NotificationEmail must be a valid email address.");

        if (string.IsNullOrWhiteSpace(options.FromEmail))
            failures.Add("Support:FromEmail is required when Support:Enabled is true. Set via Support__FromEmail.");
        else if (!IsValidEmail(options.FromEmail))
            failures.Add("Support:FromEmail must be a valid email address.");

        if (string.IsNullOrWhiteSpace(_smtp.Host))
            failures.Add("Smtp:Host is required when Support:Enabled is true. Set via Smtp__Host.");

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static bool IsValidEmail(string value) =>
        new EmailAddressAttribute().IsValid(value);
}
