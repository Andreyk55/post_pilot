namespace PostPilot.Api.Settings;

/// <summary>
/// SMTP transport settings for <see cref="Services.Email.SmtpEmailSender"/>. Bound from the
/// "Smtp" config section. Credentials are backend-only and must never be logged.
/// </summary>
public class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;

    /// <summary>SMTP submission port. 587 (STARTTLS) is the common default.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Optional. Leave blank for an anonymous/open relay.</summary>
    public string? Username { get; set; }

    /// <summary>Optional. Secret — never logged or surfaced anywhere.</summary>
    public string? Password { get; set; }

    /// <summary>Use STARTTLS (maps to <c>SmtpClient.EnableSsl</c>). Defaults to true.</summary>
    public bool UseTls { get; set; } = true;
}
