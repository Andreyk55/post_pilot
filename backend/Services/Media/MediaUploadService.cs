using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Media;

public class MediaUploadService : IMediaUploadService
{
    private readonly AppDbContext _db;
    private readonly IMediaService _mediaService;
    private readonly MediaStorageOptions _storageOpts;
    private readonly TimeSpan _presignedUploadExpiration;
    private readonly ILogger<MediaUploadService> _logger;
    private readonly IInstagramDerivativeService? _derivativeService;

    public MediaUploadService(
        AppDbContext db,
        IMediaService mediaService,
        MediaStorageOptions storageOpts,
        ILogger<MediaUploadService> logger,
        IInstagramDerivativeService? derivativeService = null)
    {
        _db = db;
        _mediaService = mediaService;
        _storageOpts = storageOpts;
        _presignedUploadExpiration = TimeSpan.FromMinutes(storageOpts.PresignedUploadExpirationMinutes);
        _logger = logger;
        _derivativeService = derivativeService;
    }

    public async Task<InitUploadResult> InitAsync(
        Guid userId,
        Guid workspaceId,
        string fileName,
        string contentType,
        long sizeBytes,
        Platform platform,
        CancellationToken cancellationToken = default)
    {
        if (!_mediaService.IsValidMediaType(contentType))
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed: {string.Join(", ", _mediaService.AllowedContentTypes)}");

        var maxSize = _mediaService.GetMaxFileSizeBytes(contentType);
        if (sizeBytes <= 0)
            throw new ArgumentException("sizeBytes must be > 0.");
        if (sizeBytes > maxSize)
            throw new ArgumentException($"File too large. Max for {contentType} is {maxSize} bytes (got {sizeBytes}).");

        // Provider-level absolute ceiling (Supabase). 0 means "no additional cap".
        if (_storageOpts.IsSupabase && _storageOpts.Supabase.MaxUploadBytes > 0 && sizeBytes > _storageOpts.Supabase.MaxUploadBytes)
            throw new ArgumentException($"File too large. Provider cap is {_storageOpts.Supabase.MaxUploadBytes} bytes (got {sizeBytes}).");

        // Pre-assign mediaId so we can embed it in the storage path. The backend chooses
        // the entire path; the frontend's fileName/contentType/platform are inputs, not paths.
        // MediaService validates the platform value against the allow-list (Facebook/Instagram).
        var mediaId = Guid.NewGuid();
        var upload = await _mediaService.GenerateUploadUrlAsync(
            userId, workspaceId, platform, mediaId, fileName, contentType, cancellationToken);

        var media = new Entities.Media
        {
            Id = mediaId,
            WorkspaceId = workspaceId,
            StorageProvider = _storageOpts.Provider,
            Bucket = _storageOpts.EffectiveBucket,
            StorageKey = upload.StorageKey,
            OriginalFileName = fileName,
            ContentType = contentType,
            SizeBytes = null,
            Status = MediaUploadStatus.PendingUpload,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = null,
        };

        _db.Media.Add(media);
        await _db.SaveChangesAsync(cancellationToken);

        var expiresAt = DateTime.UtcNow.Add(_presignedUploadExpiration);
        _logger.LogInformation(
            "Init upload mediaId={MediaId} key={Key} contentType={ContentType} sizeBytes={SizeBytes} platform={Platform}",
            media.Id, MediaValidationGateRedaction(media.StorageKey), contentType, sizeBytes, platform);

        return new InitUploadResult(
            MediaId: media.Id,
            StorageKey: upload.StorageKey,
            UploadUrl: upload.UploadUrl,
            ContentType: contentType,
            ExpiresAt: expiresAt,
            MediaType: upload.MediaType);
    }

    public async Task<CompleteUploadResult> CompleteAsync(Guid workspaceId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        var media = await _db.Media.FirstOrDefaultAsync(m => m.Id == mediaId && m.WorkspaceId == workspaceId, cancellationToken)
            ?? throw new KeyNotFoundException($"Media {mediaId} not found.");

        if (media.Status == MediaUploadStatus.Uploaded)
        {
            // Idempotent: if the client retries /complete, return the existing data.
            return new CompleteUploadResult(media.Id, media.StorageKey, media.SizeBytes ?? 0, media.ContentType, media.UploadedAt ?? DateTime.UtcNow);
        }

        var info = await _mediaService.StorageProvider.GetObjectInfoAsync(media.StorageKey, cancellationToken)
            ?? throw new InvalidOperationException("Upload not found in storage for this media item.");

        if (!string.IsNullOrEmpty(info.ContentType) &&
            !string.Equals(info.ContentType, media.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            // Warn but accept; some S3-compatible servers normalize ContentType.
            _logger.LogWarning(
                "Content-type mismatch on upload {MediaId}: declared={Declared}, observed={Observed}",
                media.Id, media.ContentType, info.ContentType);
        }

        media.Status = MediaUploadStatus.Uploaded;
        media.SizeBytes = info.SizeBytes;
        media.UploadedAt = DateTime.UtcNow;

        // Phase 3: generate an Instagram-safe JPEG derivative for PNG uploads. The original
        // is never touched (Facebook/preview keep using it); Instagram validation/publishing
        // use the derivative. Done once here, not at schedule/publish time.
        await TryGenerateInstagramDerivativeAsync(media, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Complete upload mediaId={MediaId} key={Key} sizeBytes={Size}",
            media.Id, MediaValidationGateRedaction(media.StorageKey), info.SizeBytes);

        return new CompleteUploadResult(media.Id, media.StorageKey, info.SizeBytes, media.ContentType, media.UploadedAt!.Value);
    }

    /// <summary>
    /// Generates and stores the Instagram JPEG derivative for a freshly-completed PNG upload,
    /// then records its key + metadata on the Media row. No-op for JPEG/WebP/video and when
    /// the derivative service is not wired (legacy test constructors). On any failure the
    /// partially-written derivative object is best-effort deleted and the upload-complete
    /// request fails before the uploaded Media state is persisted. That keeps the DB from
    /// referencing a derivative that does not exist.
    /// </summary>
    private async Task TryGenerateInstagramDerivativeAsync(Entities.Media media, CancellationToken cancellationToken)
    {
        if (_derivativeService == null)
            return;
        if (!_derivativeService.ShouldGenerateForContentType(media.ContentType))
            return;
        // Re-entrancy guard: never regenerate if we already have one.
        if (!string.IsNullOrEmpty(media.InstagramImageStorageKey))
            return;

        var derivativeKey = _derivativeService.BuildDerivativeKey(media.StorageKey);
        var derivativeUploaded = false;
        string? localPath = null;

        try
        {
            localPath = await _mediaService.GetLocalFilePathAsync(media.StorageKey);
            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                throw new InvalidOperationException(
                    $"Original bytes not retrievable for PNG media {media.Id}; cannot generate Instagram derivative.");
            }

            InstagramDerivativeResult derivative;
            await using (var source = File.OpenRead(localPath))
            {
                derivative = await _derivativeService.GenerateAsync(source, cancellationToken);
            }

            await using (var jpeg = derivative.JpegBytes)
            {
                derivativeUploaded = true;
                await _mediaService.StorageProvider.UploadObjectAsync(
                    derivativeKey, jpeg, derivative.MimeType, cancellationToken);
            }

            media.InstagramImageStorageKey = derivativeKey;
            media.InstagramImageMimeType = derivative.MimeType;
            media.InstagramImageSizeBytes = derivative.SizeBytes;
            media.InstagramImageWidth = derivative.Width;
            media.InstagramImageHeight = derivative.Height;
            media.InstagramImageGeneratedAt = DateTime.UtcNow;
            _logger.LogInformation(
                "Generated Instagram derivative for mediaId={MediaId} key={DerivKey} {Width}x{Height} sizeBytes={Size}",
                media.Id, MediaValidationGateRedaction(derivativeKey),
                derivative.Width, derivative.Height, derivative.SizeBytes);
        }
        catch (Exception ex)
        {
            // Roll back to a consistent state: drop any partially-written derivative object
            // and leave the Media row's derivative columns null. The original upload remains
            // valid for Facebook; Instagram is simply blocked until a JPEG is provided.
            _logger.LogError(ex,
                "Failed to generate Instagram derivative for mediaId={MediaId}; rolling back derivative state.",
                media.Id);

            if (derivativeUploaded)
            {
                try
                {
                    await _mediaService.StorageProvider.DeleteAsync(derivativeKey, cancellationToken);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx,
                        "Best-effort cleanup of partial Instagram derivative failed for mediaId={MediaId}.",
                        media.Id);
                }
            }

            // Ensure no derivative metadata is persisted.
            media.InstagramImageStorageKey = null;
            media.InstagramImageMimeType = null;
            media.InstagramImageSizeBytes = null;
            media.InstagramImageWidth = null;
            media.InstagramImageHeight = null;
            media.InstagramImageGeneratedAt = null;

            throw new InvalidOperationException(
                "Failed to generate an Instagram-ready JPEG derivative for this PNG upload. Try again or upload a JPEG.", ex);
        }
        finally
        {
            _mediaService.TryCleanupTempLocalPath(localPath);
        }
    }

    // Local redaction so derivative keys never appear raw in logs (mirrors the gate's RedactKey).
    private static string MediaValidationGateRedaction(string? key) =>
        Validation.MediaValidationGate.RedactKey(key);

    public async Task<bool> DeleteAsync(Guid workspaceId, Guid mediaId, CancellationToken cancellationToken = default)
    {
        var media = await _db.Media.FirstOrDefaultAsync(m => m.Id == mediaId && m.WorkspaceId == workspaceId, cancellationToken);
        if (media is null)
            return false;

        if (media.Status != MediaUploadStatus.Deleted)
        {
            media.Status = MediaUploadStatus.Deleted;
            await _db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await _mediaService.StorageProvider.DeleteAsync(media.StorageKey, cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort: the row is marked deleted regardless. Manual cleanup is
            // available via the storage console if needed.
            _logger.LogWarning(ex, "Best-effort delete failed for storage key {Key}",
                MediaValidationGateRedaction(media.StorageKey));
        }

        return true;
    }
}
