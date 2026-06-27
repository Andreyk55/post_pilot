using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Support;

/// <summary>
/// Default <see cref="ISupportContactService"/>.
///
/// Validation rules (MVP):
///   • Subject required, trimmed, ≤ <see cref="ValidationLimits.SupportSubjectMaxLength"/>.
///   • Message required, trimmed, ≤ <see cref="ValidationLimits.SupportMessageMaxLength"/>.
///   • Whitespace-only subject/message is rejected (treated as empty after trim).
///   • Category optional; if provided it must be a defined <see cref="SupportCategory"/>.
///   • Per-user rate limit: at most <see cref="ValidationLimits.SupportMaxRequestsPerWindow"/>
///     accepted within the last <see cref="ValidationLimits.SupportRateLimitWindowHours"/> hour(s).
///
/// Always creates the row with <see cref="SupportContactStatus.New"/>. The DB row is the
/// source of truth and is saved FIRST; an internal email notification is then sent
/// best-effort via <see cref="ISupportNotificationService"/>. Email is only a heads-up — if
/// it fails the row is kept, the failure is recorded on the row + logged, and the user still
/// gets a success response (their message was received).
/// </summary>
public sealed class SupportContactService : ISupportContactService
{
    private readonly AppDbContext _db;
    private readonly ISupportNotificationService _notifications;
    private readonly ILogger<SupportContactService> _logger;

    public SupportContactService(
        AppDbContext db,
        ISupportNotificationService notifications,
        ILogger<SupportContactService> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<SupportContactResponse> CreateAsync(
        Guid authenticatedUserId,
        Guid? workspaceId,
        CreateSupportContactRequest request,
        CancellationToken ct)
    {
        // Trim first so length checks and storage see the normalized value, and a
        // whitespace-only field collapses to empty and fails the "required" check.
        var subject = request.Subject?.Trim() ?? string.Empty;
        var message = request.Message?.Trim() ?? string.Empty;

        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrEmpty(subject))
        {
            errors["subject"] = ["Subject is required."];
        }
        else if (subject.Length > ValidationLimits.SupportSubjectMaxLength)
        {
            errors["subject"] = [$"Subject must not exceed {ValidationLimits.SupportSubjectMaxLength} characters."];
        }

        if (string.IsNullOrEmpty(message))
        {
            errors["message"] = ["Message is required."];
        }
        else if (message.Length > ValidationLimits.SupportMessageMaxLength)
        {
            errors["message"] = [$"Message must not exceed {ValidationLimits.SupportMessageMaxLength} characters."];
        }

        // Category is optional. When present it must be a known enum member — guards
        // against a raw out-of-range integer slipping past model binding.
        if (request.Category is { } category && !Enum.IsDefined(category))
        {
            errors["category"] = ["Unknown support category."];
        }

        if (errors.Count > 0)
        {
            throw new SupportValidationException(errors);
        }

        await EnforceRateLimitAsync(authenticatedUserId, ct);

        var now = DateTime.UtcNow;
        var entity = new SupportContactRequest
        {
            Id = Guid.NewGuid(),
            UserId = authenticatedUserId,
            WorkspaceId = workspaceId,
            Category = request.Category,
            Subject = subject,
            Message = message,
            Status = SupportContactStatus.New,
            CreatedAt = now,
        };

        _db.SupportContactRequests.Add(entity);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Support contact request {RequestId} created for user {UserId} (workspace {WorkspaceId}, category {Category}).",
            entity.Id, authenticatedUserId, workspaceId, request.Category);

        // Best-effort internal notification. The request is already safely persisted, so a
        // mail failure must NOT propagate — it only updates the email-status flag on the row.
        await NotifyBestEffortAsync(entity, authenticatedUserId, ct);

        return new SupportContactResponse(entity.Id, entity.Status.ToString(), entity.CreatedAt);
    }

    /// <summary>
    /// Attempts the support-inbox email and records the outcome on <paramref name="entity"/>.
    /// Never throws: the user's request is already saved, so any notification problem is
    /// swallowed (logged + stored as a Failed status) rather than surfaced.
    /// </summary>
    private async Task NotifyBestEffortAsync(SupportContactRequest entity, Guid userId, CancellationToken ct)
    {
        // Notifications disabled → leave EmailNotificationStatus = NotAttempted.
        if (!_notifications.IsEnabled)
        {
            return;
        }

        var user = await _db.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
        {
            // Should not happen (the request is FK'd to a real, authenticated user), but
            // never block on it — leave NotAttempted and move on.
            _logger.LogWarning(
                "Support notification skipped for request {RequestId}: user {UserId} not found.",
                entity.Id, userId);
            return;
        }

        try
        {
            await _notifications.SendSupportRequestNotificationAsync(entity, user, ct);
            entity.EmailNotificationStatus = SupportEmailNotificationStatus.Sent;
            entity.EmailNotificationSentAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            entity.EmailNotificationStatus = SupportEmailNotificationStatus.Failed;
            entity.EmailNotificationError = SafeError(ex);
            _logger.LogError(ex,
                "Support email notification failed for request {RequestId}; the request is saved and unaffected.",
                entity.Id);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The support request itself is already committed; failing to persist the
            // email-status flag must not surface to the user either.
            _logger.LogError(ex,
                "Failed to persist email-notification status for support request {RequestId}.", entity.Id);
        }
    }

    /// <summary>
    /// Short, safe failure summary for storage: exception type + message only (no stack
    /// trace, no secrets), truncated to the column cap.
    /// </summary>
    private static string SafeError(Exception ex)
    {
        var summary = $"{ex.GetType().Name}: {ex.Message}";
        return summary.Length > ValidationLimits.SupportEmailNotificationErrorMaxLength
            ? summary[..ValidationLimits.SupportEmailNotificationErrorMaxLength]
            : summary;
    }

    private async Task EnforceRateLimitAsync(Guid userId, CancellationToken ct)
    {
        var windowStart = DateTime.UtcNow.AddHours(-ValidationLimits.SupportRateLimitWindowHours);
        var recentCount = await _db.SupportContactRequests
            .CountAsync(r => r.UserId == userId && r.CreatedAt >= windowStart, ct);

        if (recentCount >= ValidationLimits.SupportMaxRequestsPerWindow)
        {
            _logger.LogWarning(
                "Support contact rate limit hit for user {UserId}: {Count} requests in the last {Hours}h.",
                userId, recentCount, ValidationLimits.SupportRateLimitWindowHours);
            throw new SupportRateLimitExceededException(
                "You have sent too many support messages recently. Please try again later.");
        }
    }
}
