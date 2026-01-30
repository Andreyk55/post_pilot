using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Ai;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/ai")]
public class AiMediaController : ControllerBase
{
    private readonly IMediaAiService _mediaAiService;
    private readonly IAiRateLimiter _rateLimiter;
    private readonly ILogger<AiMediaController> _logger;

    // TODO: Replace with real user authentication
    private static readonly Guid CurrentUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public AiMediaController(
        IMediaAiService mediaAiService,
        IAiRateLimiter rateLimiter,
        ILogger<AiMediaController> logger)
    {
        _mediaAiService = mediaAiService;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    /// <summary>
    /// Process media with AI assistance (caption ideas, quality check, alt text, thumbnails).
    /// </summary>
    [HttpPost("media")]
    [ProducesResponseType(typeof(AiMediaCaptionIdeasResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AiImageQualityCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AiAltTextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AiThumbnailSuggestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ProcessMedia(
        [FromBody] AiMediaRequest request,
        CancellationToken cancellationToken)
    {
        // Validate request
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // Check rate limit (thumbnail suggest is free, doesn't use AI)
        if (request.Action != AiMediaAction.ThumbnailSuggest)
        {
            var canProceed = await _rateLimiter.TryAcquireAsync(CurrentUserId, cancellationToken);
            if (!canProceed)
            {
                _logger.LogWarning("Rate limit exceeded for user {UserId}", CurrentUserId);

                return Problem(
                    title: "Rate limit exceeded",
                    detail: "AI quota reached (free tier). Try again tomorrow or enable billing.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        try
        {
            return request.Action switch
            {
                AiMediaAction.CaptionIdeas =>
                    Ok(await _mediaAiService.GenerateImageCaptionIdeasAsync(
                        request.AssetUrl,
                        request.Platform,
                        request.Text,
                        request.Language,
                        cancellationToken)),

                AiMediaAction.ImageQualityCheck =>
                    Ok(await _mediaAiService.CheckImageQualityAsync(
                        request.AssetUrl,
                        cancellationToken)),

                AiMediaAction.AltText =>
                    Ok(await _mediaAiService.GenerateAltTextAsync(
                        request.AssetUrl,
                        cancellationToken)),

                AiMediaAction.VideoCaptionIdeas =>
                    Ok(await _mediaAiService.GenerateVideoCaptionIdeasAsync(
                        request.AssetUrl,
                        request.Platform,
                        request.Text,
                        request.Language,
                        cancellationToken)),

                AiMediaAction.ThumbnailSuggest =>
                    Ok(await _mediaAiService.SuggestThumbnailsAsync(
                        request.AssetUrl,
                        cancellationToken)),

                _ => BadRequest(new { error = $"Unknown action: {request.Action}" })
            };
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            return Problem(
                title: "AI quota exceeded",
                detail: "AI quota reached (free tier). Try again tomorrow or enable billing.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 413 || ex.StatusCode == 400)
        {
            return Problem(
                title: "Media processing error",
                detail: "Media too large or unsupported format.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 504)
        {
            return Problem(
                title: "Request timed out",
                detail: "The AI service took too long to respond. Please try again.",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (GeminiApiException ex)
        {
            _logger.LogError(ex, "Gemini API error: {Message}, Status: {StatusCode}", ex.Message, ex.StatusCode);

            return Problem(
                title: "AI service unavailable",
                detail: "The AI service is temporarily unavailable. Please try again later.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("FFmpeg"))
        {
            _logger.LogWarning(ex, "FFmpeg not available for video processing");

            return Problem(
                title: "Video processing unavailable",
                detail: "Video processing is not available. FFmpeg is required for video analysis.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning(ex, "Asset not found: {AssetUrl}", request.AssetUrl);

            return Problem(
                title: "Asset not found",
                detail: "The specified media asset could not be found.",
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing AI media request");

            return Problem(
                title: "Internal error",
                detail: "An unexpected error occurred. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Process pre-extracted video frames for thumbnail selection.
    /// Frames are extracted client-side and sent as base64 data URLs.
    /// This approach works in Lambda without FFmpeg dependency.
    /// </summary>
    [HttpPost("media/thumbnails")]
    [ProducesResponseType(typeof(AiThumbnailSuggestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ProcessThumbnailFrames(
        [FromBody] AiThumbnailFramesRequest request,
        CancellationToken cancellationToken)
    {
        // Validate request
        if (request.Frames == null || request.Frames.Count == 0)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["frames"] = new[] { "At least one frame is required." }
                }));
        }

        if (request.Frames.Count > 10)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["frames"] = new[] { "Maximum 10 frames allowed." }
                }));
        }

        foreach (var (frame, index) in request.Frames.Select((f, i) => (f, i)))
        {
            if (string.IsNullOrWhiteSpace(frame.ImageData))
            {
                return ValidationProblem(new ValidationProblemDetails(
                    new Dictionary<string, string[]>
                    {
                        [$"frames[{index}].imageData"] = new[] { "Image data is required." }
                    }));
            }

            if (!frame.ImageData.StartsWith("data:image/"))
            {
                return ValidationProblem(new ValidationProblemDetails(
                    new Dictionary<string, string[]>
                    {
                        [$"frames[{index}].imageData"] = new[] { "Image data must be a valid data URL." }
                    }));
            }
        }

        try
        {
            var response = await _mediaAiService.ProcessClientExtractedFramesAsync(
                request.Frames,
                cancellationToken);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing thumbnail frames");

            return Problem(
                title: "Processing error",
                detail: "Failed to process thumbnail frames.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Generate caption ideas for a video using a pre-extracted frame.
    /// Frame is extracted client-side and sent as base64 data URL.
    /// This approach works in Lambda without FFmpeg dependency.
    /// </summary>
    [HttpPost("media/video-captions")]
    [ProducesResponseType(typeof(AiMediaCaptionIdeasResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ProcessVideoCaptionIdeas(
        [FromBody] AiVideoCaptionIdeasRequest request,
        CancellationToken cancellationToken)
    {
        // Validate request
        if (string.IsNullOrWhiteSpace(request.FrameData))
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["frameData"] = new[] { "Frame data is required." }
                }));
        }

        if (!request.FrameData.StartsWith("data:image/"))
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["frameData"] = new[] { "Frame data must be a valid data URL." }
                }));
        }

        if (!Enum.IsDefined(request.Platform))
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]>
                {
                    ["platform"] = new[] { "Invalid platform value." }
                }));
        }

        // Check rate limit
        var canProceed = await _rateLimiter.TryAcquireAsync(CurrentUserId, cancellationToken);
        if (!canProceed)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}", CurrentUserId);

            return Problem(
                title: "Rate limit exceeded",
                detail: "AI quota reached (free tier). Try again tomorrow or enable billing.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            var response = await _mediaAiService.GenerateVideoCaptionIdeasFromFrameAsync(
                request.FrameData,
                request.Platform,
                request.Text,
                request.Language,
                cancellationToken);

            return Ok(response);
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            return Problem(
                title: "AI quota exceeded",
                detail: "AI quota reached (free tier). Try again tomorrow or enable billing.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (GeminiApiException ex)
        {
            _logger.LogError(ex, "Gemini API error: {Message}, Status: {StatusCode}", ex.Message, ex.StatusCode);

            return Problem(
                title: "AI service unavailable",
                detail: "The AI service is temporarily unavailable. Please try again later.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing video caption ideas");

            return Problem(
                title: "Processing error",
                detail: "Failed to generate video caption ideas.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Dictionary<string, string[]> ValidateRequest(AiMediaRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.AssetUrl))
        {
            errors["assetUrl"] = new[] { "Asset URL is required." };
        }

        if (string.IsNullOrWhiteSpace(request.AssetType))
        {
            errors["assetType"] = new[] { "Asset type is required." };
        }
        else if (request.AssetType.ToLower() != "image" && request.AssetType.ToLower() != "video")
        {
            errors["assetType"] = new[] { "Asset type must be 'image' or 'video'." };
        }

        // Validate action is appropriate for asset type
        if (!string.IsNullOrWhiteSpace(request.AssetType))
        {
            var isImage = request.AssetType.ToLower() == "image";
            var isVideo = request.AssetType.ToLower() == "video";

            var imageOnlyActions = new[] { AiMediaAction.CaptionIdeas, AiMediaAction.ImageQualityCheck, AiMediaAction.AltText };
            var videoOnlyActions = new[] { AiMediaAction.VideoCaptionIdeas, AiMediaAction.ThumbnailSuggest };

            if (isImage && videoOnlyActions.Contains(request.Action))
            {
                errors["action"] = new[] { $"Action '{request.Action}' is only available for videos." };
            }

            if (isVideo && imageOnlyActions.Contains(request.Action))
            {
                errors["action"] = new[] { $"Action '{request.Action}' is only available for images." };
            }
        }

        if (!Enum.IsDefined(request.Action))
        {
            errors["action"] = new[] { "Invalid action value." };
        }

        if (!Enum.IsDefined(request.Platform))
        {
            errors["platform"] = new[] { "Invalid platform value." };
        }

        // Text is optional but has max length if provided
        if (!string.IsNullOrEmpty(request.Text) && request.Text.Length > 5000)
        {
            errors["text"] = new[] { "Text must not exceed 5000 characters." };
        }

        return errors;
    }
}
