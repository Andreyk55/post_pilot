using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Email;
using PostPilot.Api.Services.Support;
using PostPilot.Api.Settings;
using PostPilot.Api.Tests.TestHelpers;
using Xunit;

namespace PostPilot.Api.Tests.Services.Support;

/// <summary>
/// Support email NOTIFICATION building/sending: routes to the configured internal inbox,
/// uses the configured from-address, and includes the operational context support needs.
/// The destination address comes only from config — it is never derived from user input.
/// </summary>
public class SupportNotificationServiceTests
{
    private const string NotifyTo = "support-inbox@internal.example";
    private const string FromAddr = "noreply@postpilot.example";

    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private readonly FakeEmailSender _sender = new();

    private SupportNotificationService NewService(bool enabled = true) =>
        new(
            _sender,
            new SupportNotificationOptions
            {
                Enabled = enabled,
                NotificationEmail = NotifyTo,
                FromEmail = FromAddr,
            },
            NullLogger<SupportNotificationService>.Instance);

    private static SupportContactRequest NewRequest(
        SupportCategory? category = SupportCategory.DataDeletion,
        Guid? workspaceId = null) =>
        new()
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000c1"),
            UserId = UserId,
            WorkspaceId = workspaceId,
            Category = category,
            Subject = "Data deletion question",
            Message = "I need help with deleting my data.",
            Status = SupportContactStatus.New,
            CreatedAt = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc),
        };

    private static AppUser NewUser() => new()
    {
        Id = UserId, Email = "person@example.com", DisplayName = "Person Example",
        AuthProvider = "google", ExternalAuthUserId = "sub-1",
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public void IsEnabled_reflects_options()
    {
        Assert.True(NewService(enabled: true).IsEnabled);
        Assert.False(NewService(enabled: false).IsEnabled);
    }

    [Fact]
    public async Task Sends_to_configured_notification_email_from_configured_from_email()
    {
        await NewService().SendSupportRequestNotificationAsync(NewRequest(), NewUser(), CancellationToken.None);

        var msg = Assert.Single(_sender.Sent);
        Assert.Equal(NotifyTo, msg.To);
        Assert.Equal(FromAddr, msg.From);
    }

    [Fact]
    public async Task Subject_includes_support_context_category_and_subject()
    {
        await NewService().SendSupportRequestNotificationAsync(NewRequest(), NewUser(), CancellationToken.None);

        var subject = _sender.LastMessage!.Subject;
        Assert.Contains("[PostPilot Support]", subject);
        Assert.Contains("DataDeletion", subject);
        Assert.Contains("Data deletion question", subject);
    }

    [Fact]
    public async Task Body_includes_request_id_category_subject_message_user_and_workspace()
    {
        var request = NewRequest(workspaceId: WorkspaceId);
        var user = NewUser();

        await NewService().SendSupportRequestNotificationAsync(request, user, CancellationToken.None);

        var body = _sender.LastMessage!.TextBody;
        Assert.Contains(request.Id.ToString(), body);
        Assert.Contains("DataDeletion", body);
        Assert.Contains("Data deletion question", body);
        Assert.Contains("I need help with deleting my data.", body);
        Assert.Contains(UserId.ToString(), body);
        Assert.Contains(user.Email, body);
        Assert.Contains(user.DisplayName, body);
        Assert.Contains(WorkspaceId.ToString(), body);
        Assert.Contains("2026-06-27", body);
    }

    [Fact]
    public async Task Null_category_renders_as_General()
    {
        await NewService().SendSupportRequestNotificationAsync(
            NewRequest(category: null), NewUser(), CancellationToken.None);

        Assert.Contains("General", _sender.LastMessage!.Subject);
        Assert.Contains("Category: General", _sender.LastMessage!.TextBody);
    }

    [Fact]
    public async Task Missing_workspace_renders_as_none()
    {
        await NewService().SendSupportRequestNotificationAsync(
            NewRequest(workspaceId: null), NewUser(), CancellationToken.None);

        Assert.Contains("Workspace ID: (none)", _sender.LastMessage!.TextBody);
    }

    [Fact]
    public async Task Body_is_plain_text_only_no_html()
    {
        await NewService().SendSupportRequestNotificationAsync(NewRequest(), NewUser(), CancellationToken.None);
        Assert.Null(_sender.LastMessage!.HtmlBody);
    }
}
