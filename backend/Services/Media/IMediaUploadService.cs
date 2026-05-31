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
    /// <param name="userId">Authoritative authenticated PostPilot user id from the session —
    /// never trusted from the client. The caller MUST have verified this user has access to
    /// <paramref name="workspaceId"/>. Used as the leading <c>users/{userId}</c> segment of the
    /// storage key.</param>
    /// <param name="workspaceId">Authoritative workspace from the session — never trusted from the client.</param>
    /// <param name="fileName">Original file name from the client. Used for the storage key's basename
    /// and for audit; sanitized server-side, NEVER trusted as a path.</param>
    /// <param name="contentType">MIME type declared by the client.</param>
    /// <param name="sizeBytes">Declared file size in bytes.</param>
    /// <param name="platform">Target publishing platform (Facebook or Instagram). Required —
    /// MVP assumption is one upload belongs to one platform only, so storage layout is
    /// partitioned by platform.</param>
    Task<InitUploadResult> InitAsync(
        Guid userId,
        Guid workspaceId,
        string fileName,
        string contentType,
        long sizeBytes,
        Platform platform,
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
