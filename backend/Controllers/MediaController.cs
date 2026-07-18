using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;

namespace PostPilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IMediaUploadService _uploadService;
    private readonly IMediaValidationService _validationService;
    private readonly IMediaValidationGate _mediaGate;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly AppDbContext _db;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        IMediaService mediaService,
        IMediaUploadService uploadService,
        IMediaValidationService validationService,
        IMediaValidationGate mediaGate,
        ICurrentWorkspaceProvider currentWorkspace,
        AppDbContext db,
        ILogger<MediaController> logger)
    {
        _mediaService = mediaService;
        _uploadService = uploadService;
        _validationService = validationService;
        _mediaGate = mediaGate;
        _currentWorkspace = currentWorkspace;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a mediaId to its owning <see cref="Entities.Media"/> row, scoped to the current
    /// workspace. Returns null when the row does not exist OR belongs to a different workspace —
    /// callers must treat both cases identically (safe 404, never reveal which one it was).
    /// </summary>
    private Task<Entities.Media?> GetOwnedMediaAsync(Guid mediaId, Guid workspaceId, CancellationToken ct) =>
        _db.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mediaId && m.WorkspaceId == workspaceId, ct);

    /// <summary>
    /// Builds the frontend-safe preview URL for a media item. The frontend only ever learns
    /// this URL (or the bare mediaId) — never the underlying StorageKey.
    /// </summary>
    private string BuildMediaFileUrl(Guid mediaId, string? variant = null)
    {
        var query = variant is null ? string.Empty : $"?variant={variant}";
        if (Request?.Host.HasValue == true && !string.IsNullOrWhiteSpace(Request.Scheme))
            return $"{Request.Scheme}://{Request.Host}/api/media/{mediaId}/file{query}";
        return $"/api/media/{mediaId}/file{query}";
    }

    // ============================================
    // NEW UPLOAD FLOW: /uploads/init + /uploads/complete
    // ============================================

    /// <summary>
    /// Step 1 of the direct-upload flow. Creates a Media row in PendingUpload status
    /// and returns a presigned PUT URL the client should upload the bytes to directly.
    /// </summary>
    [HttpPost("uploads/init")]
    public async Task<ActionResult<InitUploadResponse>> InitUpload([FromBody] InitUploadRequest request, CancellationToken ct)
    {
        if (request.Platform is null)
            return BadRequest(new { error = "Platform is required. Allowed values: Facebook, Instagram." });

        try
        {
            // GetCurrentWorkspaceAsync re-checks WorkspaceMember in the DB, so this both
            // resolves the authenticated user id and verifies that user has access to the
            // workspace before we mint an upload URL. The user id becomes the leading
            // users/{userId} storage-key segment; the client never supplies it.
            var current = await _currentWorkspace.GetCurrentWorkspaceAsync(ct);
            var result = await _uploadService.InitAsync(
                current.UserId,
                current.WorkspaceId,
                request.FileName,
                request.ContentType,
                request.SizeBytes,
                platform: request.Platform.Value,
                cancellationToken: ct);
            return Ok(new InitUploadResponse(
                MediaId: result.MediaId,
                UploadUrl: result.UploadUrl,
                Method: "PUT",
                ContentType: result.ContentType,
                ExpiresAt: result.ExpiresAt,
                MediaType: result.MediaType.ToString(),
                PreviewUrl: BuildMediaFileUrl(result.MediaId)
            ));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (NotImplementedException ex)
        {
            return StatusCode(501, new { error = ex.Message });
        }
        catch (MediaUploadQuotaExceededException ex)
        {
            return StatusCode(
                StatusCodes.Status429TooManyRequests,
                new ProblemDetails
                {
                    Title = "Media upload quota exceeded",
                    Detail = ex.Message,
                    Status = StatusCodes.Status429TooManyRequests,
                    Extensions =
                    {
                        ["code"] = ex.Result.ErrorCode ?? MediaUploadQuotaExceededException.DefaultErrorCode,
                        ["limit"] = ex.Result.Limit,
                        ["used"] = ex.Result.Used,
                        ["remaining"] = ex.Result.Remaining,
                        ["resetAtUtc"] = ex.Result.PeriodEndUtc,
                    }
                });
        }
    }

    /// <summary>
    /// Step 2 of the direct-upload flow. Verifies the uploaded object exists in storage
    /// (single HEAD round-trip), captures its real size, and flips the Media row to Uploaded.
    /// Idempotent: a second call returns the existing state.
    /// </summary>
    [HttpPost("uploads/complete")]
    public async Task<ActionResult<CompleteUploadResponse>> CompleteUpload([FromBody] CompleteUploadRequest request, CancellationToken ct)
    {
        try
        {
            var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
            var result = await _uploadService.CompleteAsync(workspaceId, request.MediaId, ct);
            return Ok(new CompleteUploadResponse(
                MediaId: result.MediaId,
                SizeBytes: result.SizeBytes,
                ContentType: result.ContentType,
                UploadedAt: result.UploadedAt,
                PreviewUrl: BuildMediaFileUrl(result.MediaId)
            ));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Upload not yet present in storage.
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Marks the Media row as Deleted and best-effort removes the object from storage.
    /// </summary>
    [HttpDelete("{mediaId:guid}")]
    public async Task<IActionResult> DeleteMedia(Guid mediaId, CancellationToken ct)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
        var removed = await _uploadService.DeleteAsync(workspaceId, mediaId, ct);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>
    /// Gets the media upload constraints (allowed types and max sizes).
    /// </summary>
    [HttpGet("constraints")]
    public ActionResult<MediaConstraintsResponse> GetConstraints()
    {
        return Ok(new MediaConstraintsResponse(
            _mediaService.AllowedImageTypes.ToArray(),
            _mediaService.AllowedVideoTypes.ToArray(),
            _mediaService.MaxImageFileSizeBytes,
            _mediaService.MaxVideoFileSizeBytes
        ));
    }

    /// <summary>
    /// Streams a media file by its <see cref="Entities.Media"/> id. Requires the normal
    /// authenticated app session (controller-level <c>[Authorize]</c> — no anonymous access)
    /// and verifies the media row belongs to the caller's CURRENT workspace, resolved
    /// server-side via <see cref="ICurrentWorkspaceProvider"/>. The frontend only ever learns
    /// the mediaId (and this URL) — never the underlying StorageKey.
    ///
    /// <para>
    /// <c>?variant=thumbnail</c> serves the derived thumbnail (<c>Media.ThumbnailStorageKey</c>)
    /// instead of the original asset; omitted/any other value serves the original
    /// (<c>Media.StorageKey</c>). Unknown/foreign/missing media all return the same 404 so the
    /// response never discloses whether a mediaId exists in another workspace.
    /// </para>
    ///
    /// <para>
    /// Replaces the former anonymous <c>GET /api/media/files/{*storageKey}</c> route (removed).
    /// Publishing does not depend on this route: <see cref="Services.Media.IMediaService.GetPublishingUrlAsync"/>
    /// hands Meta a short-lived signed URL straight from the object store (Supabase/S3) at
    /// publish time. See docs/public-media-route.md for the historical rationale and migration.
    /// </para>
    /// </summary>
    [HttpGet("{mediaId:guid}/file")]
    public async Task<IActionResult> GetMediaFile(Guid mediaId, [FromQuery] string? variant, CancellationToken ct)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
        var media = await GetOwnedMediaAsync(mediaId, workspaceId, ct);
        if (media == null)
        {
            return NotFound(new { error = "Media not found" });
        }

        var isThumbnail = string.Equals(variant, "thumbnail", StringComparison.OrdinalIgnoreCase);
        string storageKey;
        string contentType;
        if (isThumbnail)
        {
            if (string.IsNullOrEmpty(media.ThumbnailStorageKey))
                return NotFound(new { error = "Media not found" });
            storageKey = media.ThumbnailStorageKey;
            contentType = string.IsNullOrWhiteSpace(media.ThumbnailMimeType) ? "application/octet-stream" : media.ThumbnailMimeType;
        }
        else
        {
            storageKey = media.StorageKey;
            contentType = string.IsNullOrWhiteSpace(media.ContentType) ? "application/octet-stream" : media.ContentType;
        }

        var stream = await _mediaService.StorageProvider.OpenReadAsync(storageKey, ct);
        if (stream == null)
        {
            return NotFound(new { error = "Media not found" });
        }

        var isKnownRenderable = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

        // Never let the browser MIME-sniff a different (possibly executable) type than declared.
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        if (!isKnownRenderable)
        {
            // Unrecognized/unsafe content type → generic binary, forced download so it can
            // never be rendered/executed in the browser context.
            Response.Headers["Content-Disposition"] = "attachment";
            return File(stream, "application/octet-stream");
        }

        if (string.Equals(contentType, "video/mp4", StringComparison.OrdinalIgnoreCase))
        {
            return File(stream, contentType, enableRangeProcessing: true);
        }

        return File(stream, contentType);
    }

    /// <summary>
    /// Local mode endpoint for serving extracted video frames.
    /// These are generated by the AI thumbnail suggestion feature.
    /// Route: GET /api/media/frames/{filename}
    /// </summary>
    [HttpGet("frames/{filename}")]
    [AllowAnonymous]
    public IActionResult GetFrame(string filename)
    {
        // Only available in local mode
        if (_mediaService.RunMode != AppRunMode.Local)
        {
            return NotFound(new { error = "Direct file access only available in local mode" });
        }

        var framesDirectory = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "uploads", "frames"));
        var framesRootWithSep = framesDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // Reject traversal: empty, absolute/rooted, or anything resolving outside the frames dir.
        if (string.IsNullOrWhiteSpace(filename) || Path.IsPathRooted(filename))
        {
            return NotFound(new { error = "Frame not found" });
        }

        string framePath;
        try
        {
            framePath = Path.GetFullPath(Path.Combine(framesDirectory, filename));
        }
        catch
        {
            return NotFound(new { error = "Frame not found" });
        }

        if (!framePath.StartsWith(framesRootWithSep, StringComparison.OrdinalIgnoreCase)
            || !System.IO.File.Exists(framePath))
        {
            return NotFound(new { error = "Frame not found" });
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var stream = new FileStream(framePath, FileMode.Open, FileAccess.Read);
        return File(stream, "image/jpeg");
    }

    // ============================================
    // STATELESS MEDIA VALIDATION ENDPOINTS
    // ============================================

    /// <summary>
    /// Validates a media file by its storage key for a specific platform and placement.
    /// This is a stateless operation - no database record is created.
    /// </summary>
    [HttpPost("validate")]
    public async Task<ActionResult<MediaValidationResult>> ValidateMedia(
        [FromBody] ValidateMediaByKeyRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation("=== VALIDATE ENDPOINT HIT === MediaId: {MediaId}, MimeType: {Mime}, Platform: {Platform}, Placement: {Placement}",
            request.MediaId, request.MimeType, request.Platform, request.Placement);

        // Determine media type from MIME type
        var mediaType = _mediaService.GetMediaType(request.MimeType);
        if (mediaType == MediaType.None)
        {
            return BadRequest(new { error = $"Invalid MIME type: {request.MimeType}" });
        }

        // Workspace ownership check: MediaId is supplied by the client, so we must resolve it
        // to a Media row owned by the current workspace before doing anything that touches
        // storage (download for validation, even just a HEAD). The StorageKey never leaves
        // the backend.
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
        var media = await GetOwnedMediaAsync(request.MediaId, workspaceId, ct);
        if (media == null)
        {
            _logger.LogWarning(
                "ValidateMedia: mediaId {MediaId} not found in workspace {WorkspaceId}",
                request.MediaId, workspaceId);
            return NotFound(new { error = "Media file not found" });
        }

        _logger.LogInformation(
            "Starting validation for {MediaType} mediaId {MediaId}, Platform: {Platform}, Placement: {Placement}",
            mediaType, request.MediaId, request.Platform, request.Placement);

        // Route through the SAME authoritative gate used at create/update/publish time. This is
        // what makes the advisory status match the eventual enforcement exactly: it validates
        // images AND videos, and it is derivative-aware (an Instagram PNG is validated against
        // its JPEG derivative, so a valid PNG is never shown as invalid). The client-supplied
        // MimeType is only used to classify image-vs-video; the gate re-derives the authoritative
        // MIME/size from the Media row.
        var result = await _mediaGate.ValidateForDisplayAsync(
            workspaceId,
            new MediaGateItem(media.StorageKey, mediaType, 0),
            new MediaGateTarget(request.Platform, request.Placement),
            ct,
            request.Carousel);

        _logger.LogInformation(
            "Validation completed for mediaId {MediaId}: Status={Status}, Errors={ErrorCount}, Warnings={WarningCount}",
            request.MediaId, result.Status, result.Errors.Length, result.Warnings.Length);

        return Ok(result);
    }

    /// <summary>
    /// Extracts metadata from a media file by its storage key.
    /// This is a stateless operation - no database record is created.
    /// </summary>
    [HttpPost("extract-metadata")]
    public async Task<ActionResult<ExtractedMediaMetadata>> ExtractMetadata(
        [FromBody] ExtractMetadataRequest request,
        CancellationToken ct)
    {
        // Determine media type from MIME type
        var mediaType = _mediaService.GetMediaType(request.MimeType);
        if (mediaType == MediaType.None)
        {
            return BadRequest(new { error = $"Invalid MIME type: {request.MimeType}" });
        }

        // Workspace ownership check: see ValidateMedia for the rationale. The StorageKey never
        // leaves the backend — only the resolved Media row's key is used internally.
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
        var media = await GetOwnedMediaAsync(request.MediaId, workspaceId, ct);
        if (media == null)
        {
            _logger.LogWarning(
                "ExtractMetadata: mediaId {MediaId} not found in workspace {WorkspaceId}",
                request.MediaId, workspaceId);
            return NotFound(new { error = "Media file not found" });
        }

        // Get file path from storage key. For S3-compatible storage this downloads
        // a temp copy; the finally below deletes it. For LocalDisk it returns the
        // real path and the cleanup helper is a no-op.
        var filePath = await _mediaService.GetLocalFilePathAsync(media.StorageKey);
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "Media file not found" });
        }

        try
        {
            var metadata = await _validationService.ExtractMetadataFromFileAsync(filePath, mediaType);
            if (metadata == null)
            {
                return BadRequest(new { error = "Failed to extract metadata" });
            }

            return Ok(metadata);
        }
        finally
        {
            _mediaService.TryCleanupTempLocalPath(filePath);
        }
    }

    /// <summary>
    /// Gets the validation rules for a specific platform, placement, and media type.
    /// Useful for frontend pre-validation.
    /// </summary>
    [HttpGet("validation-rules")]
    public ActionResult<MediaValidationRuleDto> GetValidationRules(
        [FromQuery] Platform platform,
        [FromQuery] Placement placement,
        [FromQuery] MediaType mediaType,
        [FromQuery] bool carousel = false)
    {
        // carousel=true returns the carousel per-item rule where one differs (Instagram Feed
        // video: 60s cap instead of 180s). Combinations with no carousel override return the
        // normal single-item rule.
        var rules = MediaValidationRules.GetRules(platform, placement, mediaType, carousel);
        if (rules == null)
        {
            return NotFound(new { error = $"No rules defined for {platform}/{placement}/{mediaType}" });
        }

        return Ok(new MediaValidationRuleDto(
            rules.AllowedMimeTypes,
            rules.MaxBytes,
            rules.MinWidth,
            rules.MinHeight,
            rules.MaxWidth,
            rules.MaxHeight,
            rules.AspectRatioMin,
            rules.AspectRatioMax,
            rules.DurationMinSeconds,
            rules.DurationMaxSeconds,
            rules.RecommendedWidth,
            rules.RecommendedHeight));
    }
}

public record MediaConstraintsResponse(
    string[] AllowedImageTypes,
    string[] AllowedVideoTypes,
    long MaxImageFileSizeBytes,
    long MaxVideoFileSizeBytes
);

/// <summary>
/// Request to validate media by mediaId (stateless — no DB write). The frontend never
/// supplies a StorageKey; the server resolves it internally from the Media row.
/// </summary>
/// <param name="Carousel">
/// True when the composer is validating this item as part of a multi-item carousel, so the
/// advisory status reflects the carousel per-item rules (currently the Instagram Feed video 60s
/// cap vs the 180s single-video cap). Optional; defaults to false (single item).
/// </param>
public record ValidateMediaByKeyRequest(
    Guid MediaId,
    string MimeType,
    Platform Platform,
    Placement Placement,
    bool Carousel = false
);

/// <summary>
/// Request to extract metadata from a media file by mediaId.
/// </summary>
public record ExtractMetadataRequest(
    Guid MediaId,
    string MimeType
);

/// <summary>
/// Step 1 of the direct upload flow. Client declares the file it intends to upload;
/// server returns a presigned URL and creates a Media row to track it.
///
/// <para>
/// MVP assumption: each upload belongs to ONE platform only. <see cref="Platform"/> is
/// required; the server maps it to the storage path segment
/// (Facebook → <c>meta-facebook</c>, Instagram → <c>meta-instagram</c>). Any other
/// platform value is rejected. The frontend MUST NOT send a storage key or path —
/// only file metadata + platform.
/// </para>
/// </summary>
public record InitUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes,
    Platform? Platform = null
);

/// <summary>
/// Response for the presigned-upload step. Deliberately omits StorageKey — the frontend
/// only ever learns MediaId and the authenticated PreviewUrl built from it.
/// </summary>
public record InitUploadResponse(
    Guid MediaId,
    string UploadUrl,
    string Method,
    string ContentType,
    DateTime ExpiresAt,
    string MediaType,
    string PreviewUrl
);

public record CompleteUploadRequest(
    Guid MediaId
);

/// <summary>
/// Response for the upload-complete step. Deliberately omits StorageKey — see
/// <see cref="InitUploadResponse"/>.
/// </summary>
public record CompleteUploadResponse(
    Guid MediaId,
    long SizeBytes,
    string ContentType,
    DateTime UploadedAt,
    string PreviewUrl
);

