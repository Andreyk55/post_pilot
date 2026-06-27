using Microsoft.Extensions.Options;
using PostPilot.Api.Settings;
using PostPilot.Api.Settings.Validators;
using Xunit;

namespace PostPilot.Api.Tests.Settings.Validators;

/// <summary>
/// Startup options policy for support email notifications: disabled is always valid; when
/// enabled the destination/from addresses and an SMTP host are required (username/password
/// are not — anonymous relays are allowed).
/// </summary>
public class SupportNotificationOptionsValidatorTests
{
    private static SupportNotificationOptionsValidator Validator(string smtpHost) =>
        new(new SmtpOptions { Host = smtpHost });

    private static SupportNotificationOptions Enabled(
        string notify = "support@internal.example", string from = "noreply@postpilot.example") =>
        new() { Enabled = true, NotificationEmail = notify, FromEmail = from };

    [Fact]
    public void Disabled_is_always_valid_even_with_empty_config()
    {
        var result = Validator(smtpHost: "")
            .Validate(null, new SupportNotificationOptions { Enabled = false });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Enabled_with_all_required_present_is_valid()
    {
        var result = Validator(smtpHost: "smtp.example.com").Validate(null, Enabled());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Enabled_missing_notification_email_fails()
    {
        var result = Validator(smtpHost: "smtp.example.com").Validate(null, Enabled(notify: ""));
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("NotificationEmail"));
    }

    [Fact]
    public void Enabled_missing_from_email_fails()
    {
        var result = Validator(smtpHost: "smtp.example.com").Validate(null, Enabled(from: ""));
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("FromEmail"));
    }

    [Fact]
    public void Enabled_invalid_email_fails()
    {
        var result = Validator(smtpHost: "smtp.example.com").Validate(null, Enabled(notify: "not-an-email"));
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("valid email"));
    }

    [Fact]
    public void Enabled_missing_smtp_host_fails()
    {
        var result = Validator(smtpHost: "").Validate(null, Enabled());
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Smtp:Host"));
    }
}
