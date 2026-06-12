using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Default <see cref="IMediaValidationGate"/>. Resolves media bytes via the storage
/// provider, derives the authoritative MIME/size from the workspace's <c>Media</c> rows,
/// and runs the shared <see cref="IMediaValidationService"/> rules per (item, target).
/// </summary>
public class MediaValidationGate : IMediaValidationGate
{
    private readonly AppDbContext _db;
    private readonly IMediaService _mediaService;
    private readonly IMediaValidationService _validationService;
    private readonly ILogger<MediaValidationGate> _logger;

    public MediaValidationGate(
        AppDbContext db,
        IMediaService mediaService,
        IMediaValidationService validationService,
        ILogger<MediaValidationGate> logger)
    {
        _db = db;
        _mediaService = mediaService;
        _validationService = validationService;
        _logger = logger;
    }

    public async Task<MediaGateResult> ValidateAsync(
        Guid workspaceId,
        IReadOnlyList<MediaGateItem> items,
        IReadOnlyList<MediaGateTarget> targets,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<MediaGateError>();

        foreach (var item in items)
        {
            // Phase 2: images only. Videos pass through untouched (no rule change for them here).
            if (item.MediaType != MediaType.Image)
                continue;

            // We can only validate media we actually own as a storage key. Legacy external
            // URLs (http...) and non-storage values are passed through — we have no bytes to
            // decode and must not block historical content.
            if (!_mediaService.IsStorageKey(item.StorageKeyOrUrl))
            {
                _logger.LogInformation(
                    "Media gate: skipping non-storage-key media at order {Order} ({Key})",
                    item.Order, RedactKey(item.StorageKeyOrUrl));
                continue;
            }

            // Authoritative MIME + size come from the Media row, NOT the client. The row is
            // workspace-scoped, so this also re-checks ownership of the key.
            var media = await _db.Media
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    m => m.StorageKey == item.StorageKeyOrUrl && m.WorkspaceId == workspaceId,
                    cancellationToken);

            if (media == null)
            {
                // Key not owned by this workspace (or never tracked). Don't block on it here —
                // post creation already enforces workspace ownership of targets, and blocking
                // untracked legacy keys would break old posts. Skip with a warning log.
                _logger.LogWarning(
                    "Media gate: no Media row for key {Key} in workspace {WorkspaceId}; skipping validation",
                    RedactKey(item.StorageKeyOrUrl), workspaceId);
                continue;
            }

            string? localPath = null;
            try
            {
                localPath = await _mediaService.GetLocalFilePathAsync(item.StorageKeyOrUrl);
                if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
                {
                    _logger.LogWarning(
                        "Media gate: bytes not retrievable for key {Key}; skipping validation",
                        RedactKey(item.StorageKeyOrUrl));
                    continue;
                }

                var sizeBytes = media.SizeBytes ?? new FileInfo(localPath).Length;

                foreach (var target in targets)
                {
                    var result = await _validationService.ValidateFileAsync(
                        localPath,
                        media.ContentType,
                        sizeBytes,
                        MediaType.Image,
                        target.Platform,
                        target.Placement);

                    // Warnings never block.
                    if (result.Status != ValidationStatus.Invalid)
                        continue;

                    foreach (var e in result.Errors)
                    {
                        errors.Add(new MediaGateError(
                            Order: item.Order,
                            StorageKeyRedacted: RedactKey(item.StorageKeyOrUrl),
                            Platform: target.Platform,
                            Placement: target.Placement,
                            Code: e.Code,
                            Field: e.Field,
                            Message: e.Message));
                    }
                }
            }
            finally
            {
                _mediaService.TryCleanupTempLocalPath(localPath);
            }
        }

        return new MediaGateResult(errors);
    }

    public async Task<string?> ValidateSingleAsync(
        Guid workspaceId,
        MediaGateItem item,
        MediaGateTarget target,
        CancellationToken cancellationToken = default)
    {
        var result = await ValidateAsync(workspaceId, new[] { item }, new[] { target }, cancellationToken);
        return result.IsValid ? null : result.Errors[0].Message;
    }

    /// <summary>
    /// Redacts a storage key for logging — keys are capabilities (high-entropy mediaId),
    /// so we keep only the leading scope segment and a short filename tail. Mirrors the
    /// publishers' RedactKey so log hygiene is consistent across the codebase.
    /// </summary>
    internal static string RedactKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(empty)";

        if (key.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // A full URL slipped in (legacy external media). Drop the query (signed tokens)
            // and keep scheme://host + short path tail.
            if (Uri.TryCreate(key, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath;
                var urlTail = path.Length > 12 ? path[^12..] : path;
                return $"{uri.Scheme}://{uri.Host}/...{urlTail}";
            }
        }

        var prefix = key.Split('/', 2)[0];
        var tail = key.Length > 12 ? key[^12..] : key;
        return $"{prefix}/...{tail}";
    }
}
