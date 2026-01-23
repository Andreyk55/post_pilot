namespace PostPilot.Api.Services.Media;

/// <summary>
/// Service for handling media upload and download operations.
/// </summary>
public interface IMediaService
{
    /// <summary>
    /// Generates a pre-signed URL for uploading a file directly to S3.
    /// </summary>
    /// <param name="fileName">Original file name (used to extract extension)</param>
    /// <param name="contentType">MIME type of the file</param>
    /// <returns>Upload URL result containing the pre-signed URL and S3 key</returns>
    Task<UploadUrlResult> GenerateUploadUrlAsync(string fileName, string contentType);

    /// <summary>
    /// Generates a pre-signed URL for downloading a file from S3.
    /// </summary>
    /// <param name="s3Key">The S3 object key</param>
    /// <param name="expiration">How long the URL should be valid</param>
    /// <returns>Pre-signed download URL</returns>
    string GenerateDownloadUrl(string s3Key, TimeSpan expiration);

    /// <summary>
    /// Determines if the given media URL is an S3 key (vs an external URL).
    /// </summary>
    bool IsS3Key(string? mediaUrl);

    /// <summary>
    /// Validates if the content type is an allowed image type.
    /// </summary>
    bool IsValidImageType(string contentType);

    /// <summary>
    /// Gets the list of allowed content types.
    /// </summary>
    IReadOnlyCollection<string> AllowedContentTypes { get; }

    /// <summary>
    /// Gets the maximum allowed file size in bytes.
    /// </summary>
    long MaxFileSizeBytes { get; }
}

/// <summary>
/// Result of generating an upload URL.
/// </summary>
public record UploadUrlResult(
    /// <summary>
    /// Pre-signed URL for uploading the file.
    /// </summary>
    string UploadUrl,

    /// <summary>
    /// S3 key where the file will be stored.
    /// </summary>
    string S3Key
);
