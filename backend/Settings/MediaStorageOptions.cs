namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration for the object storage backend.
/// Bound from "MediaStorage" config section.
///
/// Provider values:
///   "local-disk"     — files on the API container's filesystem (default; no MinIO required).
///   "s3-compatible"  — any S3-compatible endpoint (MinIO, S3, R2, Spaces, B2, Wasabi).
///
/// InternalEndpoint vs PublicUploadEndpoint:
///   - InternalEndpoint is what the API/Worker hit for HeadObject/GetObject/DeleteObject.
///     Inside Docker this is e.g. http://minio:9000.
///   - PublicUploadEndpoint is signed into presigned upload URLs that go back to the browser.
///     The browser cannot resolve "minio:9000"; it must receive e.g. http://localhost:9000.
/// </summary>
public class MediaStorageOptions
{
    public const string SectionName = "MediaStorage";

    public string Provider { get; set; } = "local-disk";

    public string Bucket { get; set; } = string.Empty;

    public string InternalEndpoint { get; set; } = string.Empty;

    public string PublicUploadEndpoint { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public bool UseSSL { get; set; }

    public int PresignedUploadExpirationMinutes { get; set; } = 15;

    public bool IsLocalDisk => string.Equals(Provider, "local-disk", StringComparison.OrdinalIgnoreCase);
    public bool IsS3Compatible => string.Equals(Provider, "s3-compatible", StringComparison.OrdinalIgnoreCase);
}
