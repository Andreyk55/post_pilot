namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Resolved asset information for AI processing.
/// </summary>
public record ResolvedAsset(
    /// <summary>
    /// The raw bytes of the asset.
    /// </summary>
    byte[] Bytes,

    /// <summary>
    /// The MIME type of the asset (e.g., "image/jpeg", "video/mp4").
    /// </summary>
    string MimeType
);

/// <summary>
/// Service for resolving asset URLs to bytes for AI processing.
/// </summary>
public interface IAssetResolver
{
    /// <summary>
    /// Resolves an asset URL (S3 key or external URL) to raw bytes.
    /// </summary>
    /// <param name="assetUrl">The asset URL or S3 key</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Resolved asset with bytes and MIME type</returns>
    Task<ResolvedAsset> ResolveAsync(string assetUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a publicly accessible URL for an asset.
    /// For S3 keys, generates a download URL; for external URLs, returns as-is.
    /// </summary>
    /// <param name="assetUrl">The asset URL or S3 key</param>
    /// <returns>Publicly accessible URL</returns>
    string GetPublicUrl(string assetUrl);
}
