using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Support;
using Xunit;

namespace PostPilot.Api.Tests.Services.Support;

/// <summary>
/// Support ("Contact Us") service: validation, trimming, length caps, per-user rate
/// limiting, that every row is created as <see cref="SupportContactStatus.New"/> with the
/// supplied (auth-derived) UserId and optional WorkspaceId, and that the best-effort email
/// notification never undoes or fails the saved request.
/// </summary>
public class SupportContactServiceTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid WorkspaceId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    private readonly AppDbContext _db;
    private readonly Mock<ISupportNotificationService> _notifications = new();
    private readonly SupportContactService _service;

    public SupportContactServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        // The authenticated sender exists (rows are FK'd to a real AppUser).
        _db.AppUsers.Add(new AppUser
        {
            Id = UserId, Email = "person@example.com", DisplayName = "Person",
            AuthProvider = "google", ExternalAuthUserId = "sub-1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        // Default: notifications enabled and succeeding. Individual tests override.
        _notifications.SetupGet(n => n.IsEnabled).Returns(true);
        _notifications
            .Setup(n => n.SendSupportRequestNotificationAsync(
                It.IsAny<SupportContactRequest>(), It.IsAny<AppUser>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new SupportContactService(
            _db, _notifications.Object, NullLogger<SupportContactService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static CreateSupportContactRequest Req(
        string? subject = "Help me", string? message = "Something is broken", SupportCategory? category = null) =>
        new(category, subject, message);

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_request_with_status_New_and_stored_user_and_workspace()
    {
        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);

        Assert.Equal("New", response.Status);
        Assert.NotEqual(Guid.Empty, response.Id);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Equal(SupportContactStatus.New, saved.Status);
        Assert.Equal(UserId, saved.UserId);
        Assert.Equal(WorkspaceId, saved.WorkspaceId);
        Assert.Equal(response.Id, saved.Id);
    }

    [Fact]
    public async Task Stores_null_workspace_when_none_provided()
    {
        await _service.CreateAsync(UserId, null, Req(), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Null(saved.WorkspaceId);
    }

    [Fact]
    public async Task Persists_optional_category_when_provided()
    {
        await _service.CreateAsync(
            UserId, WorkspaceId, Req(category: SupportCategory.DataDeletion), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Equal(SupportCategory.DataDeletion, saved.Category);
    }

    [Fact]
    public async Task Null_category_is_allowed()
    {
        await _service.CreateAsync(UserId, WorkspaceId, Req(category: null), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Null(saved.Category);
    }

    [Fact]
    public async Task Trims_subject_and_message()
    {
        await _service.CreateAsync(
            UserId, WorkspaceId, Req(subject: "  Hi  ", message: "  body  "), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync();
        Assert.Equal("Hi", saved.Subject);
        Assert.Equal("body", saved.Message);
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Subject_is_required(string? subject)
    {
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(subject: subject), CancellationToken.None));
        Assert.Contains("subject", ex.Errors.Keys);
        Assert.Empty(await _db.SupportContactRequests.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Message_is_required(string? message)
    {
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(message: message), CancellationToken.None));
        Assert.Contains("message", ex.Errors.Keys);
        Assert.Empty(await _db.SupportContactRequests.ToListAsync());
    }

    [Fact]
    public async Task Subject_max_length_is_enforced()
    {
        var tooLong = new string('s', ValidationLimits.SupportSubjectMaxLength + 1);
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(subject: tooLong), CancellationToken.None));
        Assert.Contains("subject", ex.Errors.Keys);
    }

    [Fact]
    public async Task Message_max_length_is_enforced()
    {
        var tooLong = new string('m', ValidationLimits.SupportMessageMaxLength + 1);
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(message: tooLong), CancellationToken.None));
        Assert.Contains("message", ex.Errors.Keys);
    }

    [Fact]
    public async Task Subject_at_max_length_is_accepted()
    {
        var atMax = new string('s', ValidationLimits.SupportSubjectMaxLength);
        await _service.CreateAsync(UserId, WorkspaceId, Req(subject: atMax), CancellationToken.None);
        Assert.Equal(1, await _db.SupportContactRequests.CountAsync());
    }

    [Fact]
    public async Task Unknown_category_is_rejected()
    {
        var bogus = (SupportCategory)999;
        var ex = await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(category: bogus), CancellationToken.None));
        Assert.Contains("category", ex.Errors.Keys);
    }

    // ── Rate limiting ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Per_user_rate_limit_blocks_after_window_cap()
    {
        for (var i = 0; i < ValidationLimits.SupportMaxRequestsPerWindow; i++)
        {
            await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);
        }

        await Assert.ThrowsAsync<SupportRateLimitExceededException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None));

        // Exactly the cap was persisted — the over-limit attempt created nothing.
        Assert.Equal(
            ValidationLimits.SupportMaxRequestsPerWindow,
            await _db.SupportContactRequests.CountAsync());
    }

    [Fact]
    public async Task Rate_limit_is_per_user_other_users_unaffected()
    {
        var otherUser = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
        for (var i = 0; i < ValidationLimits.SupportMaxRequestsPerWindow; i++)
        {
            await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);
        }

        // A different user is not blocked by the first user's volume.
        var response = await _service.CreateAsync(otherUser, null, Req(), CancellationToken.None);
        Assert.Equal("New", response.Status);
    }

    [Fact]
    public async Task Old_requests_outside_window_do_not_count_toward_limit()
    {
        // Seed cap-worth of requests dated before the rolling window.
        var stale = DateTime.UtcNow.AddHours(-(ValidationLimits.SupportRateLimitWindowHours + 1));
        for (var i = 0; i < ValidationLimits.SupportMaxRequestsPerWindow; i++)
        {
            _db.SupportContactRequests.Add(new SupportContactRequest
            {
                Id = Guid.NewGuid(), UserId = UserId, Subject = "old", Message = "old",
                Status = SupportContactStatus.New, CreatedAt = stale,
            });
        }
        await _db.SaveChangesAsync();

        // A fresh request still succeeds because the stale ones are outside the window.
        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);
        Assert.Equal("New", response.Status);
    }

    // ── Email notification (best-effort) ─────────────────────────────────────────

    [Fact]
    public async Task Saves_row_and_attempts_email_notification()
    {
        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);

        // DB row saved …
        Assert.Equal(1, await _db.SupportContactRequests.CountAsync(r => r.Id == response.Id));
        // … and a notification was attempted.
        _notifications.Verify(n => n.SendSupportRequestNotificationAsync(
            It.Is<SupportContactRequest>(r => r.Id == response.Id),
            It.Is<AppUser>(u => u.Id == UserId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Notification_is_not_attempted_when_validation_fails()
    {
        await Assert.ThrowsAsync<SupportValidationException>(
            () => _service.CreateAsync(UserId, WorkspaceId, Req(subject: ""), CancellationToken.None));

        _notifications.Verify(n => n.SendSupportRequestNotificationAsync(
            It.IsAny<SupportContactRequest>(), It.IsAny<AppUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Email_success_marks_status_Sent_and_sets_sent_at()
    {
        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync(r => r.Id == response.Id);
        Assert.Equal(SupportEmailNotificationStatus.Sent, saved.EmailNotificationStatus);
        Assert.NotNull(saved.EmailNotificationSentAt);
        Assert.Null(saved.EmailNotificationError);
    }

    [Fact]
    public async Task Email_failure_keeps_row_and_does_not_throw()
    {
        _notifications
            .Setup(n => n.SendSupportRequestNotificationAsync(
                It.IsAny<SupportContactRequest>(), It.IsAny<AppUser>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        // No throw — the user still gets a success response.
        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);
        Assert.Equal("New", response.Status);

        // The DB row survives the email failure.
        Assert.Equal(1, await _db.SupportContactRequests.CountAsync(r => r.Id == response.Id));
    }

    [Fact]
    public async Task Email_failure_marks_status_Failed_with_safe_error_only()
    {
        _notifications
            .Setup(n => n.SendSupportRequestNotificationAsync(
                It.IsAny<SupportContactRequest>(), It.IsAny<AppUser>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp connection refused"));

        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync(r => r.Id == response.Id);
        Assert.Equal(SupportEmailNotificationStatus.Failed, saved.EmailNotificationStatus);
        Assert.Null(saved.EmailNotificationSentAt);
        Assert.NotNull(saved.EmailNotificationError);
        // Short, safe summary: exception type + message, no stack trace.
        Assert.Contains("InvalidOperationException", saved.EmailNotificationError!);
        Assert.DoesNotContain("   at ", saved.EmailNotificationError!);
        Assert.True(saved.EmailNotificationError!.Length <= ValidationLimits.SupportEmailNotificationErrorMaxLength);
    }

    [Fact]
    public async Task Notifications_disabled_leaves_status_NotAttempted_and_skips_send()
    {
        _notifications.SetupGet(n => n.IsEnabled).Returns(false);

        var response = await _service.CreateAsync(UserId, WorkspaceId, Req(), CancellationToken.None);

        var saved = await _db.SupportContactRequests.SingleAsync(r => r.Id == response.Id);
        Assert.Equal(SupportEmailNotificationStatus.NotAttempted, saved.EmailNotificationStatus);
        _notifications.Verify(n => n.SendSupportRequestNotificationAsync(
            It.IsAny<SupportContactRequest>(), It.IsAny<AppUser>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
