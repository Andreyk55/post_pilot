using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Scheduling;

/// <summary>Machine-readable codes for schedule validation failures (surfaced in ProblemDetails.code).</summary>
public static class SchedulingCodes
{
    public const string ScheduledAtInPast = "SCHEDULED_AT_IN_PAST";
    public const string ScheduledAtTooFar = "SCHEDULED_AT_TOO_FAR";
    public const string ScheduledPostLimitReached = "SCHEDULED_POST_LIMIT_REACHED";
}

/// <summary>A single schedule validation failure: a stable code plus a user-facing message.</summary>
public readonly record struct ScheduleValidationError(string Code, string Message);

/// <summary>
/// Single reusable server-side guard for scheduling rules: past-dated schedules, far-future
/// schedules, and the per-workspace active scheduled-post cap. Used by post create/update so a
/// crafted request that bypasses the SPA is still rejected. Times are compared in UTC.
/// </summary>
public sealed class ScheduleGuard
{
    /// <summary>
    /// Statuses that count as an ACTIVE scheduled post (can still result in a publish).
    /// Publishing is deliberately excluded: it is a transient worker-held state, not a queued
    /// slot a user controls. Published/Failed/Canceled are terminal and never counted.
    /// </summary>
    internal static readonly PostStatus[] ActiveStatuses =
    {
        PostStatus.Scheduled,
        PostStatus.RetryPending,
        PostStatus.Processing,
    };

    private readonly AppDbContext _db;
    private readonly SchedulingOptions _options;

    public ScheduleGuard(AppDbContext db, SchedulingOptions options)
    {
        _db = db;
        _options = options;
    }

    /// <summary>
    /// Validates the schedule time against the past-grace and max-future windows. Returns null
    /// when acceptable. Never coerces the value — a past date is rejected, not silently bumped.
    /// </summary>
    public ScheduleValidationError? ValidateTiming(DateTime scheduledAt)
    {
        var now = DateTime.UtcNow;
        var scheduledUtc = ToUtc(scheduledAt);

        var grace = TimeSpan.FromMinutes(Math.Max(0, _options.PastGraceMinutes));
        if (scheduledUtc < now - grace)
        {
            return new ScheduleValidationError(
                SchedulingCodes.ScheduledAtInPast,
                "Scheduled time is in the past. Choose a time at or after now.");
        }

        var maxDays = _options.MaxFutureDays > 0 ? _options.MaxFutureDays : 365;
        if (scheduledUtc > now.AddDays(maxDays))
        {
            return new ScheduleValidationError(
                SchedulingCodes.ScheduledAtTooFar,
                $"Scheduled time is too far in the future. It must be within {maxDays} days from now.");
        }

        return null;
    }

    /// <summary>
    /// Enforces the per-workspace active scheduled-post cap. On update pass the edited post's id
    /// as <paramref name="excludePostId"/> so an in-place edit of an already-counted post is not
    /// rejected. Returns null when under the cap (or when the cap is non-positive = unlimited).
    /// </summary>
    public async Task<ScheduleValidationError?> ValidateActiveCapAsync(
        Guid workspaceId, Guid? excludePostId = null, CancellationToken cancellationToken = default)
    {
        var cap = _options.MaxActiveScheduledPostsPerWorkspace;
        if (cap <= 0)
        {
            return null; // Non-positive => unlimited (defensive; the cap is disabled).
        }

        var query = _db.Posts.Where(p => p.WorkspaceId == workspaceId && ActiveStatuses.Contains(p.Status));
        if (excludePostId is Guid id)
        {
            query = query.Where(p => p.Id != id);
        }

        var activeCount = await query.CountAsync(cancellationToken);
        if (activeCount >= cap)
        {
            return new ScheduleValidationError(
                SchedulingCodes.ScheduledPostLimitReached,
                $"This workspace has reached its limit of {cap} active scheduled posts. Publish, cancel, or delete some before scheduling more.");
        }

        return null;
    }

    /// <summary>
    /// Normalizes a bound DateTime to UTC to match the worker's UTC comparison
    /// (<c>ScheduledAt &lt;= DateTime.UtcNow</c>). Unspecified-kind values (JSON without a
    /// timezone) are treated as already-UTC; Local values are converted.
    /// </summary>
    private static DateTime ToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };
}
