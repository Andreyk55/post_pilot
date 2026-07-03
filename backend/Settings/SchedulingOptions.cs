namespace PostPilot.Api.Settings;

/// <summary>
/// Server-side guardrails for scheduling posts. Bound from the "Scheduling" config section.
/// Every value has a safe, finite default so the guard still works if config is missing.
/// </summary>
public class SchedulingOptions
{
    public const string SectionName = "Scheduling";

    /// <summary>
    /// How far in the past a scheduled time may be before it is rejected, in minutes.
    /// Absorbs client/server clock skew so a "publish ~now" schedule isn't rejected on a
    /// slightly slow clock. A schedule earlier than (now - this) is rejected as in-past.
    /// Default: 2 minutes.
    /// </summary>
    public int PastGraceMinutes { get; set; } = 2;

    /// <summary>
    /// Maximum number of days ahead a post may be scheduled. A schedule later than
    /// (now + this) is rejected. Default: 365 days. Non-positive falls back to 365.
    /// </summary>
    public int MaxFutureDays { get; set; } = 365;

    /// <summary>
    /// Maximum number of ACTIVE (still-publishable) scheduled posts a single workspace may
    /// hold — statuses Scheduled, RetryPending, Processing. Published/Failed/Canceled do not
    /// count. Default: 500. Non-positive means "unlimited" (the cap is skipped).
    /// </summary>
    public int MaxActiveScheduledPostsPerWorkspace { get; set; } = 500;
}
