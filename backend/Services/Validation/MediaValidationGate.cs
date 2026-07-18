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
        CancellationToken cancellationToken = default,
        bool requireOwnedStorageKey = false)
    {
        var errors = new List<MediaGateError>();

        // A post with 2+ media items is a carousel; its video items use the stricter carousel
        // per-item rules (e.g. Instagram Feed carousel video is capped at 60s vs 180s single).
        var isCarousel = items.Count >= 2;

        foreach (var item in items)
        {
            // We can only validate media we actually own as a storage key. Legacy external
            // URLs (http...) and non-storage values have no bytes to decode.
            if (!_mediaService.IsStorageKey(item.StorageKeyOrUrl))
            {
                if (requireOwnedStorageKey)
                {
                    // Enforcement path (post create/update): an external URL or non-storage
                    // value is never acceptable — a crafted request must not attach arbitrary
                    // media. Block on every target so the caller sees a consistent error.
                    AddNotOwnedErrors(errors, item, targets);
                    continue;
                }

                // Defense-in-depth path (publisher): pass historical external content through.
                _logger.LogInformation(
                    "Media gate: skipping non-storage-key media at order {Order} ({Key})",
                    item.Order, RedactKey(item.StorageKeyOrUrl));
                continue;
            }

            var media = await LoadMediaAsync(workspaceId, item.StorageKeyOrUrl, cancellationToken);
            if (media == null)
            {
                if (requireOwnedStorageKey)
                {
                    // Enforcement path: the key is unknown or owned by another workspace.
                    // Reject rather than skip so foreign/unknown keys cannot ride into a post.
                    _logger.LogWarning(
                        "Media gate: no Media row for key {Key} in workspace {WorkspaceId}; rejecting (ownership required)",
                        RedactKey(item.StorageKeyOrUrl), workspaceId);
                    AddNotOwnedErrors(errors, item, targets);
                    continue;
                }

                // Defense-in-depth path: key not owned by this workspace (or never tracked).
                // Don't block untracked legacy keys here — that would strand old posts.
                _logger.LogWarning(
                    "Media gate: no Media row for key {Key} in workspace {WorkspaceId}; skipping validation",
                    RedactKey(item.StorageKeyOrUrl), workspaceId);
                continue;
            }

            foreach (var target in targets)
            {
                var result = await ValidateResolvedAsync(media, item.MediaType, target, cancellationToken, isCarousel);

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

    public async Task<MediaValidationResult> ValidateForDisplayAsync(
        Guid workspaceId,
        MediaGateItem item,
        MediaGateTarget target,
        CancellationToken cancellationToken = default,
        bool isCarousel = false)
    {
        // Legacy external URLs / non-storage values: nothing to decode, treat as publishable
        // (mirrors ValidateAsync's pass-through so the advisory UI matches the gate exactly).
        if (!_mediaService.IsStorageKey(item.StorageKeyOrUrl))
            return Publishable();

        var media = await LoadMediaAsync(workspaceId, item.StorageKeyOrUrl, cancellationToken);
        if (media == null)
            return Publishable();

        return await ValidateResolvedAsync(media, item.MediaType, target, cancellationToken, isCarousel);
    }

    /// <summary>
    /// Emits a blocking "media not owned by this workspace" error for the given item against
    /// every target (external URL, unknown key, or foreign-workspace key). Used only on the
    /// enforcement path; the redacted key is safe to surface, the raw key is never logged/echoed.
    /// </summary>
    private static void AddNotOwnedErrors(
        List<MediaGateError> errors, MediaGateItem item, IReadOnlyList<MediaGateTarget> targets)
    {
        var redacted = RedactKey(item.StorageKeyOrUrl);
        foreach (var target in targets)
        {
            errors.Add(new MediaGateError(
                Order: item.Order,
                StorageKeyRedacted: redacted,
                Platform: target.Platform,
                Placement: target.Placement,
                Code: MediaValidationErrorCodes.MediaNotFound,
                Field: "media",
                Message: "The selected media could not be found in this workspace. Re-upload the file and try again."));
        }
    }

    private Task<Entities.Media?> LoadMediaAsync(Guid workspaceId, string storageKey, CancellationToken cancellationToken) =>
        // Authoritative MIME + size come from the Media row, NOT the client. The row is
        // workspace-scoped, so this also re-checks ownership of the key.
        _db.Media
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.StorageKey == storageKey && m.WorkspaceId == workspaceId, cancellationToken);

    /// <summary>
    /// Resolves the effective media for the target (Instagram PNG → JPEG derivative, everything
    /// else → original), downloads its bytes once, and runs the shared rule engine. Images and
    /// videos both validate here; videos extract metadata via ffprobe inside the rule engine.
    /// Returns the full <see cref="MediaValidationResult"/> so callers can surface warnings too.
    /// </summary>
    private async Task<MediaValidationResult> ValidateResolvedAsync(
        Entities.Media media, MediaType mediaType, MediaGateTarget target, CancellationToken cancellationToken,
        bool isCarouselItem = false)
    {
        var effective = EffectiveMediaResolver.Resolve(media, mediaType, target.Platform);

        if (effective.DerivativeMissing)
        {
            return Invalid(MediaValidationErrorCodes.InstagramDerivativeMissing, "media",
                "This PNG image has no Instagram-ready JPEG version yet. Re-upload the image and try again, or use a JPEG.");
        }

        string? localPath = null;
        try
        {
            localPath = await _mediaService.GetLocalFilePathAsync(effective.StorageKey);

            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
            {
                _logger.LogWarning("Media gate: bytes not retrievable for key {Key}", RedactKey(effective.StorageKey));

                if (effective.IsDerivative)
                {
                    return Invalid(MediaValidationErrorCodes.InstagramDerivativeMissing, "media",
                        "This PNG image has no retrievable Instagram-ready JPEG version. Re-upload the image and try again, or use a JPEG.");
                }

                // Original bytes unavailable (legacy/transient): don't block.
                return Publishable();
            }

            var sizeBytes = effective.SizeBytes ?? new FileInfo(localPath).Length;

            return await _validationService.ValidateFileAsync(
                localPath,
                effective.MimeType,
                sizeBytes,
                mediaType,
                target.Platform,
                target.Placement,
                isCarouselItem);
        }
        finally
        {
            _mediaService.TryCleanupTempLocalPath(localPath);
        }
    }

    private static MediaValidationResult Publishable() =>
        new(ValidationStatus.Valid, Array.Empty<MediaValidationError>(), Array.Empty<MediaValidationWarning>(), null);

    private static MediaValidationResult Invalid(string code, string field, string message) =>
        new(
            ValidationStatus.Invalid,
            new[] { new MediaValidationError(code, field, message, null, null) },
            Array.Empty<MediaValidationWarning>(),
            null);

    /// <summary>
    /// Redacts a storage key for logging; keys are capabilities (high-entropy mediaId),
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
