namespace PostPilot.Api.Enums;

/// <summary>
/// Lifecycle of a provider-level <see cref="Entities.DataDeletionRequest"/> (Meta data
/// deletion callback). Distinct from <see cref="PostStatus"/>; never reused for posts.
/// </summary>
public enum DataDeletionStatus
{
    /// <summary>Request accepted and the purge is running (or just created).</summary>
    Processing = 0,

    /// <summary>A matching connection was found and its Meta data was purged.</summary>
    Completed = 1,

    /// <summary>
    /// No matching connection/data existed (already gone, or the user deleted their
    /// PostPilot account before Meta sent the callback). Still a success — the
    /// end-state ("no Meta data for this account") is what Meta asked for.
    /// </summary>
    AlreadyDeleted = 2,

    /// <summary>An unexpected error aborted the purge. Safe, non-leaking message stored.</summary>
    Failed = 3,
}
