using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MediaController : ControllerBase
{
    private readonly IMediaService _mediaService;
    private readonly ILogger<MediaController> _logger;

    public MediaController(
        IMediaService mediaService,
        ILogger<MediaController> logger)
    {
        _mediaService = mediaService;
        _logger = logger;
    }

    /// <summary>
    /// Generates a pre-signed URL for uploading media (image or video).
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
                result.S3Key,
                result.MediaType.ToString(),
                _mediaService.AllowedImageTypes.ToArray(),
                _mediaService.AllowedVideoTypes.ToArray(),
                _mediaService.MaxImageFileSizeBytes,
                _mediaService.MaxVideoFileSizeBytes
            ));
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
    /// Local development endpoint for receiving file uploads.
    /// In production, files are uploaded directly to S3.
    /// Route: PUT /api/media/upload/{filename} where filename is just "guid.ext"
    /// </summary>
    [HttpPut("upload/{filename}")]
    [RequestSizeLimit(250 * 1024 * 1024)] // 250MB to allow for video uploads + overhead
    public async Task<IActionResult> UploadFile(string filename)
    {
        // Only available when using LocalMediaService
        if (_mediaService is not LocalMediaService localService)
        {
            return NotFound(new { error = "Direct upload only available in development mode" });
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
            await localService.SaveFileAsync(filename, Request.Body);
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
    /// Local development endpoint for serving uploaded files (images and videos).
    /// In production, files are served via S3 pre-signed URLs.
    /// Route: GET /api/media/files/{filename} where filename is just "guid.ext"
    /// </summary>
    [HttpGet("files/{filename}")]
    public IActionResult GetFile(string filename)
    {
        // Only available when using LocalMediaService
        if (_mediaService is not LocalMediaService localService)
        {
            return NotFound(new { error = "Direct file access only available in development mode" });
        }

        if (!localService.FileExists(filename))
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

        var stream = localService.OpenRead(filename);

        // For video files, enable range requests for seeking/streaming
        if (contentType == "video/mp4")
        {
            return File(stream, contentType, enableRangeProcessing: true);
        }

        return File(stream, contentType);
    }
}

public record GenerateUploadUrlRequest(
    string FileName,
    string ContentType
);

public record GenerateUploadUrlResponse(
    string UploadUrl,
    string S3Key,
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
