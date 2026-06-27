using PostPilot.Api.Enums;

namespace PostPilot.Api.Entities;

/// <summary>
/// Audit record for a provider-level data-deletion request (Meta's "Data Deletion
/// Callback"). One row per inbound callback. The row is intentionally NOT
/// foreign-keyed to <see cref="AppUser"/> / <see cref="Workspace"/> /
/// <see cref="MetaConnection"/>: those targets are hard-deleted by the purge, and
/// the audit trail must outlive them so the public status page keeps resolving the
/// confirmation code after the data is gone.
///
/// Looked up publicly by <see cref="ConfirmationCode"/> (random, URL-safe — never an
/// internal DB id). Nothing user-identifying is exposed by the status endpoint.
/// </summary>
public class DataDeletionRequest
{
    public Guid Id { get; set; }

    /// <summary>
    /// Random, URL-safe, alphanumeric public handle returned to Meta and embedded in
    /// the status URL. Unique. Not derived from any internal id.
    /// </summary>
    public required string ConfirmationCode { get; set; }

    /// <summary>Provider this deletion targets. Always <see cref="ProviderType.Meta"/> today.</summary>
    public ProviderType Provider { get; set; } = ProviderType.Meta;

    /// <summary>
    /// Stable provider-account id from the verified signed_request (Meta app-scoped
    /// user id). The lookup key for the purge. Stored for audit/debugging only; never
    /// surfaced by the public status endpoint.
    /// </summary>
    public string? ProviderAccountId { get; set; }

    /// <summary>Owning AppUser of the purged connection, captured at purge time. Null when nothing matched.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Workspace of the purged connection, captured at purge time. Null when nothing matched.</summary>
    public Guid? WorkspaceId { get; set; }

    public DataDeletionStatus Status { get; set; } = DataDeletionStatus.Processing;

    public DateTime RequestedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>Safe, non-leaking error summary when <see cref="Status"/> is Failed.</summary>
    public string? Error { get; set; }

    /// <summary>Optional non-fatal warning (e.g. best-effort storage delete failures).</summary>
    public string? Warning { get; set; }
}
