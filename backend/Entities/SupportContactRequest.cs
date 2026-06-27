using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

/// <summary>
/// A support / customer-service message sent by an AUTHENTICATED PostPilot user from the
/// in-app "Contact Us" form. One row per submission.
///
/// <para><see cref="UserId"/> is the authenticated principal — it is ALWAYS derived from
/// the session by the controller/service and never read from the request body. The row is
/// foreign-keyed to <see cref="AppUser"/> with cascade delete: full account deletion
/// removes the user's support requests with them (the MVP-endorsed behavior). This is the
/// deliberate opposite of <see cref="DataDeletionRequest"/>, which is intentionally
/// FK-free so it can outlive a provider purge — a support request has no reason to survive
/// the deletion of the user who sent it.</para>
///
/// <para><see cref="WorkspaceId"/> is a nullable SOFT pointer (no FK): we capture the
/// user's currently-selected workspace when one is available, but Contact Us must work even
/// with no workspace selected, and a later workspace deletion must not be blocked by, or
/// cascade through, a support row.</para>
/// </summary>
public class SupportContactRequest
{
    public Guid Id { get; set; }

    /// <summary>Authenticated AppUser who submitted this. FK → AppUser (cascade on delete).</summary>
    public Guid UserId { get; set; }

    /// <summary>Currently-selected workspace at submit time, when available. Soft pointer; no FK.</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>Optional self-selected topic. Null is a valid "General question".</summary>
    public SupportCategory? Category { get; set; }

    /// <summary>Trimmed subject line. Required, length-capped.</summary>
    public required string Subject { get; set; }

    /// <summary>Trimmed message body. Required, length-capped.</summary>
    public required string Message { get; set; }

    /// <summary>Triage lifecycle. MVP always creates <see cref="SupportContactStatus.New"/>.</summary>
    public SupportContactStatus Status { get; set; } = SupportContactStatus.New;

    public DateTime CreatedAt { get; set; }

    /// <summary>Set when an operator updates the request (future admin flow).</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Set when the request reaches a terminal resolved/closed state (future admin flow).</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Operator-only note. NEVER returned to the user by any endpoint.</summary>
    public string? InternalNote { get; set; }
}
