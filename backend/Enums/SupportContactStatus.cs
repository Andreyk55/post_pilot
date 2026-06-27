namespace PostPilot.Api.Enums;

/// <summary>
/// Lifecycle of an authenticated user's <see cref="Entities.SupportContactRequest"/>.
/// Distinct from <see cref="DataDeletionStatus"/> (provider data-deletion) and
/// <see cref="PostStatus"/> — never reused across those domains.
///
/// MVP only ever creates <see cref="New"/>; the remaining states exist for a future
/// internal/admin triage flow and are not set by any user-facing endpoint today.
/// </summary>
public enum SupportContactStatus
{
    /// <summary>Just submitted by the user. The only state the MVP creates.</summary>
    New = 0,

    /// <summary>An operator has picked the request up (future admin flow).</summary>
    InProgress = 1,

    /// <summary>The request was handled and the user was helped (future admin flow).</summary>
    Resolved = 2,

    /// <summary>Closed without a separate resolution (e.g. duplicate/spam; future admin flow).</summary>
    Closed = 3,
}
