using PostPilot.Api.Enums;

namespace PostPilot.Api.DTOs;

/// <summary>
/// Body for POST /api/support/contact. The ONLY accepted fields are an optional category,
/// the subject, and the message. There is deliberately no userId/accountId/email field —
/// the target user is derived solely from the auth principal, so any such value a client
/// adds to the JSON is simply not bound here and is ignored.
/// </summary>
public sealed record CreateSupportContactRequest(
    SupportCategory? Category,
    string? Subject,
    string? Message);

/// <summary>
/// Safe projection returned by POST /api/support/contact. Intentionally free of internal
/// notes, user private info, workspace ids, and any admin-only fields.
/// </summary>
public sealed record SupportContactResponse(
    Guid Id,
    string Status,
    DateTime CreatedAt);
