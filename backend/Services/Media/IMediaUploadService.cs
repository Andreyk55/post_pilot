using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Orchestrates the two-step direct upload flow:
///   1. <see cref="InitAsync"/>   — issue a presigned upload URL, record a Media row (PendingUpload).
///   2. <see cref="CompleteAsync"/> — verify the object landed in storage, flip the row to Uploaded.
///
/// Posts continue to reference media by storage key; the Media row is an audit/lifecycle ledger,
/// not part of the post→media foreign-key chain.
/// </summary>
public interface IMediaUploadService
{
    /// <summary>
    /// Step 1 of the upload flow.
    /// </summary>
    /// <param name="workspaceId">Authoritative workspace from the session — never trusted from the client.</param>
    /// <param name="fileName">Original file name from the client. Used for the storage key's basename
    /// and for audit; sanitized server-side, NEVER trusted as a path.</param>
    /// <param name="contentType">MIME type declared by the client.</param>
    /// <param name="sizeBytes">Declared file size in bytes.</param>
    /// <param name="provider">Optional identity provider this upload is for (e.g.
    /// <see cref="ProviderType.Meta"/>). When supplied together with
    /// <paramref name="providerConnectionId"/> the storage key embeds those segments
    /// for layout-level tenancy. When null, the key uses the reserved
    /// <c>providers/unassigned/connections/none</c> segments.</param>
    /// <param name="providerConnectionId">Optional id of the specific provider connection (e.g.
    /// <see cref="Entities.MetaConnection.Id"/>). Validated to belong to the same workspace.</param>
    Task<InitUploadResult> InitAsync(
        Guid workspaceId,
        string fileName,
        string contentType,
        long sizeBytes,
        ProviderType? provider = null,
        Guid? providerConnectionId = null,
        CancellationToken cancellationToken = default);

    Task<CompleteUploadResult> CompleteAsync(Guid workspaceId, Guid mediaId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid workspaceId, Guid mediaId, CancellationToken cancellationToken = default);
}

public record InitUploadResult(
    Guid MediaId,
    string StorageKey,
    string UploadUrl,
    string ContentType,
    DateTime ExpiresAt,
    MediaType MediaType
);

public record CompleteUploadResult(
    Guid MediaId,
    string StorageKey,
    long SizeBytes,
    string ContentType,
    DateTime UploadedAt
);
