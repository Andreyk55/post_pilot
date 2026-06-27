using System.Text;
using PostPilot.Api.Entities;
using PostPilot.Api.Services.Email;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Support;

/// <summary>
/// Default <see cref="ISupportNotificationService"/>. Builds a plain-text notification
/// (text is enough for the MVP — no HTML, so there is no escaping concern) summarizing a
/// new support request and emails it to the backend-configured internal inbox.
///
/// The destination/from addresses come from <see cref="SupportNotificationOptions"/> and
/// are never exposed to the user. The email includes operational context (request id,
/// category, subject, message, user id/email/name, workspace id, timestamp) so support can
/// act without opening the DB.
/// </summary>
public sealed class SupportNotificationService : ISupportNotificationService
{
    private readonly IEmailSender _emailSender;
    private readonly SupportNotificationOptions _options;
    private readonly ILogger<SupportNotificationService> _logger;

    public SupportNotificationService(
        IEmailSender emailSender,
        SupportNotificationOptions options,
        ILogger<SupportNotificationService> logger)
    {
        _emailSender = emailSender;
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled;

    public async Task SendSupportRequestNotificationAsync(
        SupportContactRequest request,
        AppUser user,
        CancellationToken ct)
    {
        var category = request.Category?.ToString() ?? "General";
        var subject = $"[PostPilot Support] {category} - {request.Subject}";

        var body = BuildTextBody(request, user, category);

        var message = new EmailMessage(
            To: _options.NotificationEmail,
            From: _options.FromEmail,
            Subject: subject,
            TextBody: body);

        await _emailSender.SendAsync(message, ct);

        _logger.LogInformation(
            "Support notification email sent for request {RequestId}.", request.Id);
    }

    private static string BuildTextBody(SupportContactRequest request, AppUser user, string category)
    {
        var sb = new StringBuilder();
        sb.AppendLine("New PostPilot support request");
        sb.AppendLine();
        sb.AppendLine($"Request ID: {request.Id}");
        sb.AppendLine($"Category: {category}");
        sb.AppendLine($"Subject: {request.Subject}");
        sb.AppendLine($"User ID: {request.UserId}");
        sb.AppendLine($"User Email: {user.Email}");
        sb.AppendLine($"User Name: {user.DisplayName}");
        sb.AppendLine($"Workspace ID: {(request.WorkspaceId?.ToString() ?? "(none)")}");
        sb.AppendLine($"Created At: {request.CreatedAt:O}");
        sb.AppendLine();
        sb.AppendLine("Message:");
        sb.AppendLine(request.Message);
        return sb.ToString();
    }
}
