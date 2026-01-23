using Microsoft.AspNetCore.Mvc;
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
    /// Generates a pre-signed URL for uploading an image.
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
                _mediaService.AllowedContentTypes.ToArray(),
                _mediaService.MaxFileSizeBytes
            ));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid upload request: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Local development endpoint for receiving file uploads.
    /// In production, files are uploaded directly to S3.
    /// Route: PUT /api/media/upload/{filename} where filename is just "guid.ext"
    /// </summary>
    [HttpPut("upload/{filename}")]
    public async Task<IActionResult> UploadFile(string filename)
    {
        // Only available when using LocalMediaService
        if (_mediaService is not LocalMediaService localService)
        {
            return NotFound(new { error = "Direct upload only available in development mode" });
        }

        if (!_mediaService.IsValidImageType(Request.ContentType ?? ""))
        {
            return BadRequest(new { error = "Invalid content type" });
        }

        // Check content length
        if (Request.ContentLength > _mediaService.MaxFileSizeBytes)
        {
            return BadRequest(new { error = $"File too large. Maximum size is {_mediaService.MaxFileSizeBytes / (1024 * 1024)}MB" });
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
    /// Local development endpoint for serving uploaded files.
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
            _ => "application/octet-stream"
        };

        var stream = localService.OpenRead(filename);
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
    string[] AllowedContentTypes,
    long MaxFileSizeBytes
);
