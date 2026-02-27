using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly IMediaValidationService _validationService;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        IMediaService mediaService,
        IMediaValidationService validationService,
        ILogger<MediaController> logger)
    {
        _mediaService = mediaService;
        _validationService = validationService;
        _logger = logger;
    }

    /// <summary>
    /// Generates a pre-signed URL for uploading media (image or video).
    /// Works in both local and server mode.
    /// </summary>
    [HttpPost("upload-url")]
    public async Task<ActionResult<GenerateUploadUrlResponse>> GenerateUploadUrl(
        [FromBody] GenerateUploadUrlRequest request)
    {
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
    /// Local mode endpoint for serving uploaded files (images and videos).
    /// In server mode, files are served via pre-signed download URLs from object storage.
    /// Route: GET /api/media/files/{filename} where filename is just "guid.ext"
    /// </summary>
    [HttpGet("files/{filename}")]
    public async Task<IActionResult> GetFile(string filename)
    {
        // Only available in local mode
        if (_mediaService.RunMode != AppRunMode.Local)
        {
            return NotFound(new { error = "Direct file access only available in local mode" });
        }

        if (!_mediaService.StorageProvider.Exists(filename))
        {
            return NotFound(new { error = "File not found" });
        }

        var extension = Path.GetExtension(filename).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

        var stream = await _mediaService.StorageProvider.OpenReadAsync(filename);
        if (stream == null)
        {
            return NotFound(new { error = "File not found" });
        }

        // For video files, enable range requests for seeking/streaming
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
        [FromBody] ValidateMediaByKeyRequest request)
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

        // Get file path from storage key
        var filePath = await _mediaService.GetLocalFilePathAsync(request.StorageKey);
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "Media file not found" });
        }

        // Get file size
        var fileInfo = new FileInfo(filePath);
        var sizeBytes = fileInfo.Length;

        _logger.LogInformation(
            "Starting validation for {MediaType} file: {StorageKey}, Platform: {Platform}, Placement: {Placement}",
            mediaType, request.StorageKey, request.Platform, request.Placement);

        // Validate the file
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

    /// <summary>
    /// Extracts metadata from a media file by its storage key.
    /// This is a stateless operation - no database record is created.
    /// </summary>
    [HttpPost("extract-metadata")]
    public async Task<ActionResult<ExtractedMediaMetadata>> ExtractMetadata(
        [FromBody] ExtractMetadataRequest request)
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

        // Get file path from storage key
        var filePath = await _mediaService.GetLocalFilePathAsync(request.StorageKey);
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            return NotFound(new { error = "Media file not found" });
        }

        var metadata = await _validationService.ExtractMetadataFromFileAsync(filePath, mediaType);
        if (metadata == null)
        {
            return BadRequest(new { error = "Failed to extract metadata" });
        }

        return Ok(metadata);
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
