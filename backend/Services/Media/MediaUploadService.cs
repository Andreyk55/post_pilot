using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Services.Media;

public class MediaUploadService : IMediaUploadService
{
    // Reserved path segments used when the upload is not yet bound to a specific
    // provider/account. Kept here as constants so the values are not duplicated
    // across MediaService, tests, and the controller surface.
    public const string UnassignedProviderSegment = "unassigned";
    public const string NoConnectionSegment = "none";

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

    public async Task<InitUploadResult> InitAsync(
        Guid workspaceId,
        string fileName,
        string contentType,
        long sizeBytes,
        ProviderType? provider = null,
        Guid? providerConnectionId = null,
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

        // ── Resolve the provider/connection segments ────────────────────────
        // The frontend may optionally select a provider connection it intends the
        // upload to belong to. If it does, we MUST verify that connection lives in
        // the same workspace the session resolves to — otherwise a member of
        // workspace A could ask for an upload URL scoped under workspace B's
        // connection, leaking the path structure (and potentially co-mingling
        // objects under another tenant's prefix).
        var (providerSegment, connectionSegment) = await ResolveProviderSegmentsAsync(
            workspaceId, provider, providerConnectionId, cancellationToken);

        // Pre-assign mediaId so we can embed it in the storage path. The backend chooses
        // the entire path — the frontend's fileName/contentType are inputs, not paths.
        var mediaId = Guid.NewGuid();
        var upload = await _mediaService.GenerateUploadUrlAsync(
            workspaceId, providerSegment, connectionSegment, mediaId, fileName, contentType, cancellationToken);

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
            "Init upload mediaId={MediaId} key={Key} contentType={ContentType} sizeBytes={SizeBytes} provider={Provider} connection={Connection}",
            media.Id, media.StorageKey, contentType, sizeBytes, providerSegment, connectionSegment);

        return new InitUploadResult(
            MediaId: media.Id,
            StorageKey: upload.StorageKey,
            UploadUrl: upload.UploadUrl,
            ContentType: contentType,
            ExpiresAt: expiresAt,
            MediaType: upload.MediaType);
    }

    /// <summary>
    /// Validates the (optional) provider + providerConnectionId pair against the current
    /// workspace and returns the path segments to use in the storage key.
    ///
    /// Rules:
    ///   - If no provider is given, segments fall back to <c>unassigned</c> / <c>none</c>.
    ///   - If a provider but no connection id is given, only the provider segment is filled.
    ///   - If a connection id is given, it MUST belong to the same workspace AND match
    ///     the provider value (if both are given). Otherwise we refuse — never silently
    ///     fall back to "unassigned", because that would mask a cross-workspace probe.
    /// </summary>
    private async Task<(string ProviderSegment, string ConnectionSegment)> ResolveProviderSegmentsAsync(
        Guid workspaceId,
        ProviderType? provider,
        Guid? providerConnectionId,
        CancellationToken cancellationToken)
    {
        if (provider is null && providerConnectionId is null)
            return (UnassignedProviderSegment, NoConnectionSegment);

        if (providerConnectionId is null)
        {
            return (provider!.Value.ToString().ToLowerInvariant(), NoConnectionSegment);
        }

        // Today the only provider connection table is MetaConnection. When LinkedIn /
        // X / TikTok land, add their lookups here keyed on `provider`.
        switch (provider ?? ProviderType.Meta)
        {
            case ProviderType.Meta:
                var meta = await _db.Set<MetaConnection>()
                    .Where(c => c.Id == providerConnectionId.Value && c.WorkspaceId == workspaceId)
                    .Select(c => new { c.Id, c.Provider })
                    .FirstOrDefaultAsync(cancellationToken);
                if (meta is null)
                {
                    // Either the connection id doesn't exist, OR it belongs to a different
                    // workspace. We collapse both to one error so callers can't distinguish
                    // "doesn't exist" from "exists but isn't yours" via timing/error text.
                    throw new UnauthorizedAccessException(
                        $"Provider connection {providerConnectionId} is not accessible from this workspace.");
                }
                return (meta.Provider.ToString().ToLowerInvariant(), meta.Id.ToString("D"));

            default:
                throw new ArgumentException(
                    $"Provider {provider} does not yet support connection-scoped uploads.");
        }
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
