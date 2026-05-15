namespace PostPilot.Api.Enums;

/// <summary>
/// Lifecycle state of a media upload, tracked by the Media side table.
/// </summary>
public enum MediaUploadStatus
{
    /// <summary>Init endpoint issued a presigned URL; bytes have not yet been confirmed in storage.</summary>
    PendingUpload,

    /// <summary>Complete endpoint verified the object exists in storage.</summary>
    Uploaded,

    /// <summary>Media was deleted (best-effort removal from storage; row kept for audit).</summary>
    Deleted
}
