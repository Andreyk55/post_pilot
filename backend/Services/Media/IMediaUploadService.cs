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
    Task<InitUploadResult> InitAsync(Guid workspaceId, string fileName, string contentType, long sizeBytes, CancellationToken cancellationToken = default);

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
