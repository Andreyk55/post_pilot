using System.Text.Json.Serialization;

namespace PostPilot.Api.DTOs;

/// <summary>
/// Response shape REQUIRED by Meta's Data Deletion Callback. Property names are
/// snake_case via explicit <see cref="JsonPropertyNameAttribute"/> so the global
/// camelCase policy does not rename them — Meta expects exactly these keys.
/// </summary>
public sealed record MetaDataDeletionCallbackResponse(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("confirmation_code")] string ConfirmationCode);

/// <summary>
/// Public status projection returned by GET /api/data-deletion/status/{code}.
/// Intentionally free of internal ids, userId, workspaceId, providerAccountId,
/// tokens, and raw error detail.
/// </summary>
public sealed record DataDeletionStatusResponse(
    string ConfirmationCode,
    string Provider,
    string Status,
    DateTime RequestedAt,
    DateTime? CompletedAt);

/// <summary>
/// Body for DELETE /api/account (and POST /api/account/delete). The only accepted
/// field is the typed confirmation phrase. Any userId/accountId in the body is
/// ignored — the target is derived solely from the auth principal.
/// </summary>
public sealed record DeleteAccountRequest(string? ConfirmationText);
