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
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly AppDbContext _db;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        IMediaService mediaService,
        IMediaUploadService uploadService,
        IMediaValidationService validationService,
        ICurrentWorkspaceProvider currentWorkspace,
        AppDbContext db,
        ILogger<MediaController> logger)
    {
        _mediaService = mediaService;
        _uploadService = uploadService;
        _validationService = validationService;
        _currentWorkspace = currentWorkspace;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns true when the storage key belongs to a Media row in the given workspace.
    /// Used by stateless endpoints (validate / extract-metadata) to refuse keys from
    /// other workspaces — otherwise a member of workspace A could probe whether a key
    /// from workspace B exists in storage, or trigger a server-side download of it.
    /// </summary>
    private Task<bool> StorageKeyBelongsToWorkspaceAsync(string storageKey, Guid workspaceId, CancellationToken ct) =>
        _db.Media.AnyAsync(m => m.StorageKey == storageKey && m.WorkspaceId == workspaceId, ct);

    /// <summary>
    /// Generates a pre-signed URL for uploading media (image or video).
    /// Works in both local and server mode.
    /// </summary>
    [Obsolete("Use POST /api/media/uploads/init instead. This endpoint does not create a Media row.")]
    [HttpPost("upload-url")]
    public async Task<ActionResult<GenerateUploadUrlResponse>> GenerateUploadUrl(
        [FromBody] GenerateUploadUrlRequest request)
    {
        _logger.LogInformation("Legacy /api/media/upload-url called. Prefer /api/media/uploads/init.");

        try
        {
            var result = await _mediaService.GenerateUploadUrlAsync(
                request.FileName,
                request.ContentType);

            return Ok(new GenerateUploadUrlResponse(
                result.UploadUrl,
                result.StorageKey,
                result.MediaType.ToString(),
                _mediaService.AllowedImageTypes.ToArray(),
                _mediaService.AllowedVideoTypes.ToArray(),
                _mediaService.MaxImageFileSizeBytes,
                _mediaService.MaxVideoFileSizeBytes
            ));
        }
        catch (NotImplementedException ex)
        {
            _logger.LogWarning("Upload URL generation is not implemented for server mode: {Message}", ex.Message);
            return StatusCode(501, new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid upload request: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
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
        try
        {
            var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
            var result = await _uploadService.InitAsync(workspaceId, request.FileName, request.ContentType, request.SizeBytes, ct);
            return Ok(new InitUploadResponse(
                MediaId: result.MediaId,
                StorageKey: result.StorageKey,
                UploadUrl: result.UploadUrl,
                Method: "PUT",
                ContentType: result.ContentType,
                ExpiresAt: result.ExpiresAt,
                MediaType: result.MediaType.ToString()
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
                StorageKey: result.StorageKey,
                SizeBytes: result.SizeBytes,
                ContentType: result.ContentType,
                UploadedAt: result.UploadedAt
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
    /// Local mode endpoint for receiving file uploads.
    /// In server mode, files are uploaded directly to object storage via pre-signed PUT URLs.
    /// Route: PUT /api/media/upload/{filename} where filename is just "guid.ext"
    /// </summary>
    [Obsolete("Local-disk direct upload. Prefer the /uploads/init presigned-PUT flow.")]
    [HttpPut("upload/{filename}")]
    [RequestSizeLimit(250 * 1024 * 1024)] // 250MB to allow for video uploads + overhead
    public async Task<IActionResult> UploadFile(string filename)
    {
        // Only available in local mode
        if (_mediaService.RunMode != AppRunMode.Local)
        {
            return NotFound(new { error = "Direct upload only available in local mode" });
        }

        var contentType = Request.ContentType ?? "";
        if (!_mediaService.IsValidMediaType(contentType))
        {
            return BadRequest(new { error = "Invalid content type. Allowed: images (JPEG, PNG, GIF) and videos (MP4)" });
        }

        // Check content length against the appropriate max size
        var maxSize = _mediaService.GetMaxFileSizeBytes(contentType);
        if (Request.ContentLength > maxSize)
        {
            var maxSizeMB = maxSize / (1024 * 1024);
            var mediaType = _mediaService.IsValidVideoType(contentType) ? "video" : "image";
            return BadRequest(new { error = $"File too large. Maximum {mediaType} size is {maxSizeMB}MB" });
        }

        try
        {
            await _mediaService.StorageProvider.SaveAsync(filename, Request.Body);
            _logger.LogInformation("File uploaded successfully: {Filename}", filename);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save uploaded file: {Filename}", filename);
            return StatusCode(500, new { error = "Failed to save file" });
        }
    }

    /// <summary>
    /// Streams a stored file by its full storage key. Catch-all route so keys like
    /// "media/{guid}.jpg" are preserved end-to-end without slicing on the client.
    /// Route: GET /api/media/files/{*storageKey}
    ///
    /// PUBLIC BY DESIGN — DO NOT add [Authorize] here without a replacement plan.
    /// During publish, this URL is handed to Meta (via App.PublicUrl) so Facebook /
    /// Instagram fetchers can pull the bytes directly. Those fetchers do not present
    /// any auth, so the route MUST stay anonymous for publishing to work.
    ///
    /// Mitigations that make the unauth surface safe in practice:
    ///   - Storage keys are "media/{guid}.{ext}", produced by IMediaService at upload
    ///     time. The guid is server-generated and never exposed except to:
    ///       (a) the workspace member who uploaded it (via /uploads/init response),
    ///       (b) Meta during publishing.
    ///   - There is no enumeration endpoint that lists keys.
    ///   - Keys are not logged in any user-visible surface.
    ///
    /// Future hardening (not yet implemented):
    ///   - Replace this with short-lived presigned URLs handed to Meta per publish.
    ///   - Add a separate authenticated /api/media/files/{key}/private route for the
    ///     SPA so we can drop the anonymous one once Meta migrates.
    /// </summary>
    [HttpGet("files/{*storageKey}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetFile(string storageKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return NotFound(new { error = "Storage key is required" });

        var extension = Path.GetExtension(storageKey).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

        var stream = await _mediaService.StorageProvider.OpenReadAsync(storageKey, ct);
        if (stream == null)
        {
            return NotFound(new { error = "File not found" });
        }

        if (contentType == "video/mp4")
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

        var framesDirectory = Path.Combine(Directory.GetCurrentDirectory(), "uploads", "frames");
        var framePath = Path.Combine(framesDirectory, filename);

        if (!System.IO.File.Exists(framePath))
        {
            return NotFound(new { error = "Frame not found" });
        }

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
        _logger.LogInformation("=== VALIDATE ENDPOINT HIT === StorageKey: {Key}, MimeType: {Mime}, Platform: {Platform}, Placement: {Placement}",
            request.StorageKey, request.MimeType, request.Platform, request.Placement);

        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            return BadRequest(new { error = "Storage key is required" });
        }

        // Determine media type from MIME type
        var mediaType = _mediaService.GetMediaType(request.MimeType);
        if (mediaType == MediaType.None)
        {
            return BadRequest(new { error = $"Invalid MIME type: {request.MimeType}" });
        }

        // Workspace ownership check: the StorageKey is supplied by the client, so we
        // must verify it points at a Media row in the current workspace before doing
        // anything that touches storage (download for validation, even just a HEAD).
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
        if (!await StorageKeyBelongsToWorkspaceAsync(request.StorageKey, workspaceId, ct))
        {
            _logger.LogWarning(
                "ValidateMedia: storage key {Key} not found in workspace {WorkspaceId}",
                request.StorageKey, workspaceId);
            return NotFound(new { error = "Media file not found" });
        }

        // Get file path from storage key. For S3-compatible storage this downloads
        // a temp copy; the finally below deletes it. For LocalDisk it returns the
        // real path and the cleanup helper is a no-op.
        var filePath = await _mediaService.GetLocalFilePathAsync(request.StorageKey);
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "Media file not found" });
        }

        try
        {
            var fileInfo = new FileInfo(filePath);
            var sizeBytes = fileInfo.Length;

            _logger.LogInformation(
                "Starting validation for {MediaType} file: {StorageKey}, Platform: {Platform}, Placement: {Placement}",
                mediaType, request.StorageKey, request.Platform, request.Placement);

            var result = await _validationService.ValidateFileAsync(
                filePath,
                request.MimeType,
                sizeBytes,
                mediaType,
                request.Platform,
                request.Placement);

            _logger.LogInformation(
                "Validation completed for {StorageKey}: Status={Status}, Errors={ErrorCount}, Warnings={WarningCount}",
                request.StorageKey, result.Status, result.Errors.Length, result.Warnings.Length);

            return Ok(result);
        }
        finally
        {
            _mediaService.TryCleanupTempLocalPath(filePath);
        }
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
        if (string.IsNullOrWhiteSpace(request.StorageKey))
        {
            return BadRequest(new { error = "Storage key is required" });
        }

        // Determine media type from MIME type
        var mediaType = _mediaService.GetMediaType(request.MimeType);
        if (mediaType == MediaType.None)
        {
            return BadRequest(new { error = $"Invalid MIME type: {request.MimeType}" });
        }

        // Workspace ownership check: see ValidateMedia for the rationale.
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(ct);
        if (!await StorageKeyBelongsToWorkspaceAsync(request.StorageKey, workspaceId, ct))
        {
            _logger.LogWarning(
                "ExtractMetadata: storage key {Key} not found in workspace {WorkspaceId}",
                request.StorageKey, workspaceId);
            return NotFound(new { error = "Media file not found" });
        }

        // Get file path from storage key. For S3-compatible storage this downloads
        // a temp copy; the finally below deletes it. For LocalDisk it returns the
        // real path and the cleanup helper is a no-op.
        var filePath = await _mediaService.GetLocalFilePathAsync(request.StorageKey);
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
        [FromQuery] MediaType mediaType)
    {
        var rules = MediaValidationRules.GetRules(platform, placement, mediaType);
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

public record GenerateUploadUrlRequest(
    string FileName,
    string ContentType
);

public record GenerateUploadUrlResponse(
    string UploadUrl,
    string StorageKey,
    string MediaType,
    string[] AllowedImageTypes,
    string[] AllowedVideoTypes,
    long MaxImageFileSizeBytes,
    long MaxVideoFileSizeBytes
);

public record MediaConstraintsResponse(
    string[] AllowedImageTypes,
    string[] AllowedVideoTypes,
    long MaxImageFileSizeBytes,
    long MaxVideoFileSizeBytes
);

/// <summary>
/// Request to validate media by storage key (stateless).
/// </summary>
public record ValidateMediaByKeyRequest(
    string StorageKey,
    string MimeType,
    Platform Platform,
    Placement Placement
);

/// <summary>
/// Request to extract metadata from a media file by storage key.
/// </summary>
public record ExtractMetadataRequest(
    string StorageKey,
    string MimeType
);

/// <summary>
/// Step 1 of the direct upload flow. Client declares the file it intends to upload;
/// server returns a presigned URL and creates a Media row to track it.
/// </summary>
public record InitUploadRequest(
    string FileName,
    string ContentType,
    long SizeBytes
);

public record InitUploadResponse(
    Guid MediaId,
    string StorageKey,
    string UploadUrl,
    string Method,
    string ContentType,
    DateTime ExpiresAt,
    string MediaType
);

public record CompleteUploadRequest(
    Guid MediaId
);

public record CompleteUploadResponse(
    Guid MediaId,
    string StorageKey,
    long SizeBytes,
    string ContentType,
    DateTime UploadedAt
);
