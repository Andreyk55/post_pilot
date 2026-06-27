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
/// Always creates the row with <see cref="SupportContactStatus.New"/>. No email is sent —
/// there is no configured email/notification provider; storing the request is the whole MVP.
/// </summary>
public sealed class SupportContactService : ISupportContactService
{
    private readonly AppDbContext _db;
    private readonly ILogger<SupportContactService> _logger;

    public SupportContactService(AppDbContext db, ILogger<SupportContactService> logger)
    {
        _db = db;
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

        return new SupportContactResponse(entity.Id, entity.Status.ToString(), entity.CreatedAt);
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
