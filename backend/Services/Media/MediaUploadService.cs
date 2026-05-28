using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
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

    public MediaUploadService(
        AppDbContext db,
        IMediaService mediaService,
        MediaStorageOptions storageOpts,
        ILogger<MediaUploadService> logger)
    {
        _db = db;
        _mediaService = mediaService;
        _storageOpts = storageOpts;
        _presignedUploadExpiration = TimeSpan.FromMinutes(storageOpts.PresignedUploadExpirationMinutes);
        _logger = logger;
    }

    public async Task<InitUploadResult> InitAsync(Guid workspaceId, string fileName, string contentType, long sizeBytes, CancellationToken cancellationToken = default)
    {
        if (!_mediaService.IsValidMediaType(contentType))
            throw new ArgumentException($"Invalid content type: {contentType}. Allowed: {string.Join(", ", _mediaService.AllowedContentTypes)}");

        var maxSize = _mediaService.GetMaxFileSizeBytes(contentType);
        if (sizeBytes <= 0)
            throw new ArgumentException("sizeBytes must be > 0.");
        if (sizeBytes > maxSize)
            throw new ArgumentException($"File too large. Max for {contentType} is {maxSize} bytes (got {sizeBytes}).");

        // Delegate to IMediaService so storage-key generation and presigning stay in one place.
        var upload = await _mediaService.GenerateUploadUrlAsync(fileName, contentType);

        var media = new Entities.Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StorageProvider = _storageOpts.Provider,
            Bucket = _storageOpts.Bucket,
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
            "Init upload mediaId={MediaId} key={Key} contentType={ContentType} sizeBytes={SizeBytes}",
            media.Id, media.StorageKey, contentType, sizeBytes);

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
            ?? throw new InvalidOperationException($"Upload not found in storage for key '{media.StorageKey}'.");

        if (!string.IsNullOrEmpty(info.ContentType) &&
            !string.Equals(info.ContentType, media.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            // Warn but accept — some S3-compatible servers normalize ContentType.
            _logger.LogWarning(
                "Content-type mismatch on upload {MediaId}: declared={Declared}, observed={Observed}",
                media.Id, media.ContentType, info.ContentType);
        }

        media.Status = MediaUploadStatus.Uploaded;
        media.SizeBytes = info.SizeBytes;
        media.UploadedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Complete upload mediaId={MediaId} key={Key} sizeBytes={Size}", media.Id, media.StorageKey, info.SizeBytes);

        return new CompleteUploadResult(media.Id, media.StorageKey, info.SizeBytes, media.ContentType, media.UploadedAt!.Value);
    }

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
            _logger.LogWarning(ex, "Best-effort delete failed for storage key {Key}", media.StorageKey);
        }

        return true;
    }
}
