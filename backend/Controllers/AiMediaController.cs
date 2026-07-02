using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Media;

namespace PostPilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/ai")]
public class AiMediaController : ControllerBase
{
    private readonly IMediaAiService _mediaAiService;
    private readonly IAiRateLimiter _rateLimiter;
    private readonly AppDbContext _db;
    private readonly IMediaOwnershipService _mediaOwnership;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly ILogger<AiMediaController> _logger;

    public AiMediaController(
        IMediaAiService mediaAiService,
        IAiRateLimiter rateLimiter,
        AppDbContext db,
        IMediaOwnershipService mediaOwnership,
        ICurrentUserProvider currentUser,
        ICurrentWorkspaceProvider currentWorkspace,
        ILogger<AiMediaController> logger)
    {
        _mediaAiService = mediaAiService;
        _rateLimiter = rateLimiter;
        _db = db;
        _mediaOwnership = mediaOwnership;
        _currentUser = currentUser;
        _currentWorkspace = currentWorkspace;
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
    [ProducesResponseType(typeof(AiMediaUnsupportedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ProcessMedia(
        [FromBody] AiMediaRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetCurrentUserId();
        // Validate request
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        var mediaSupport = ValidateMediaAiSupported(request);
        if (!mediaSupport.IsSupported)
        {
            if (mediaSupport.ReturnDisabledVideoResponse)
            {
                _logger.LogInformation("Video AI action {Action} is disabled for asset {AssetUrl}", request.Action, mediaSupport.MediaItem?.AssetUrl);
                return Ok(CreateDisabledVideoResponse(request.Action));
            }

            _logger.LogInformation(
                "Media AI is unsupported for action {Action}. Reason={Reason}",
                request.Action,
                mediaSupport.Reason);

            return Ok(new AiMediaUnsupportedResponse(request.Action, "Media AI supports a single image only."));
        }

        var mediaItem = mediaSupport.MediaItem!;

        // OWNERSHIP + SSRF GATE. AssetUrl is client-supplied. It MUST be a server-issued
        // storage key owned by the current workspace — never an external URL. This blocks
        // (a) SSRF: fetching arbitrary http/https/file/ftp/localhost/private-network targets
        // server-side, and (b) cross-workspace reads: resolving another workspace's storage
        // key. Reject before rate-limiting or resolving any bytes. The raw AssetUrl is never
        // echoed back so a caller can't probe which keys exist.
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        if (!await _mediaOwnership.IsOwnedStorageKeyAsync(mediaItem.AssetUrl, workspaceId, cancellationToken))
        {
            _logger.LogWarning(
                "AI media request rejected: asset is not a storage key owned by workspace {WorkspaceId}.",
                workspaceId);
            return NotFound(new { error = "media_not_found" });
        }

        // Check rate limit (thumbnail suggest is free, doesn't use AI)
        if (request.Action != AiMediaAction.ThumbnailSuggest)
        {
            var canProceed = await _rateLimiter.TryAcquireAsync(userId, cancellationToken);
            if (!canProceed)
            {
                _logger.LogWarning("Rate limit exceeded for user {UserId}", userId);

                return Problem(
                    title: "Rate limit exceeded",
                    detail: "AI quota reached (free tier). Try again tomorrow or enable billing.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
        }

        try
        {
            var voiceProfile = await LoadVoiceProfileForMediaActionAsync(request, userId, cancellationToken);

            return request.Action switch
            {
                AiMediaAction.CaptionIdeas =>
                    Ok(await _mediaAiService.GenerateImageCaptionIdeasAsync(
                        mediaItem.AssetUrl,
                        request.Platform,
                        request.Text,
                        request.Language,
                        voiceProfile,
                        cancellationToken)),

                AiMediaAction.ImageQualityCheck =>
                    Ok(await _mediaAiService.CheckImageQualityAsync(
                        mediaItem.AssetUrl,
                        cancellationToken)),

                AiMediaAction.AltText =>
                    Ok(await _mediaAiService.GenerateAltTextAsync(
                        mediaItem.AssetUrl,
                        cancellationToken)),

                AiMediaAction.VideoCaptionIdeas =>
                    Ok(await _mediaAiService.GenerateVideoCaptionIdeasAsync(
                        mediaItem.AssetUrl,
                        request.Platform,
                        request.Text,
                        request.Language,
                        voiceProfile,
                        cancellationToken)),

                AiMediaAction.ThumbnailSuggest =>
                    Ok(await _mediaAiService.SuggestThumbnailsAsync(
                        mediaItem.AssetUrl,
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
            _logger.LogWarning(ex, "Gemini media error for asset {AssetUrl}: Status={StatusCode}, Message={Message}",
                mediaItem.AssetUrl, ex.StatusCode, ex.Message);

            return Problem(
                title: "Media processing error",
                detail: $"Media too large or unsupported format. ({ex.Message})",
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
            _logger.LogWarning(ex, "Asset not found: {AssetUrl}", mediaItem.AssetUrl);

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

    private async Task<AiVoiceProfile?> LoadVoiceProfileForMediaActionAsync(
        AiMediaRequest request,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (!MediaActionSupportsVoiceProfile(request.Action) || !request.VoiceProfileId.HasValue)
        {
            return null;
        }

        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var voiceProfile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(
                p => p.Id == request.VoiceProfileId.Value && p.WorkspaceId == workspaceId && !p.IsDeleted,
                cancellationToken);

        if (voiceProfile == null)
        {
            _logger.LogWarning("Voice profile {ProfileId} not found for user {UserId}", request.VoiceProfileId, userId);
        }

        return voiceProfile;
    }

    private static bool MediaActionSupportsVoiceProfile(AiMediaAction action)
    {
        return action is AiMediaAction.CaptionIdeas or AiMediaAction.VideoCaptionIdeas;
    }

    /// <summary>
    /// Process pre-extracted video frames for thumbnail selection.
    /// Frames are extracted client-side and sent as base64 data URLs.
    /// This approach works in Lambda without FFmpeg dependency.
    /// </summary>
    [HttpPost("media/thumbnails")]
    [ProducesResponseType(typeof(AiThumbnailSuggestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public Task<IActionResult> ProcessThumbnailFrames(
        [FromBody] AiThumbnailFramesRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Video thumbnail AI is disabled; returning empty thumbnail response");
        return Task.FromResult<IActionResult>(
            Ok(new AiThumbnailSuggestResponse(AiMediaAction.ThumbnailSuggest, new List<AiVideoFrame>())));
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
    public Task<IActionResult> ProcessVideoCaptionIdeas(
        [FromBody] AiVideoCaptionIdeasRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Video caption AI is disabled; returning empty caption response");
        return Task.FromResult<IActionResult>(
            Ok(new AiMediaCaptionIdeasResponse(AiMediaAction.VideoCaptionIdeas, new List<AiMediaCaptionVariant>())));
    }

    private static Dictionary<string, string[]> ValidateRequest(AiMediaRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.MediaItems is null || request.MediaItems.Count == 0)
        {
            errors["mediaItems"] = new[] { "At least one media item is required." };
        }

        if (request.MediaItems is not null)
        {
            for (var index = 0; index < request.MediaItems.Count; index++)
            {
                var mediaItem = request.MediaItems[index];

                if (string.IsNullOrWhiteSpace(mediaItem.AssetUrl))
                {
                    errors[$"mediaItems[{index}].assetUrl"] = new[] { "Asset URL is required." };
                }

                if (string.IsNullOrWhiteSpace(mediaItem.AssetType))
                {
                    errors[$"mediaItems[{index}].assetType"] = new[] { "Asset type is required." };
                }
                else if (!mediaItem.AssetType.Equals("image", StringComparison.OrdinalIgnoreCase)
                    && !mediaItem.AssetType.Equals("video", StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"mediaItems[{index}].assetType"] = new[] { "Asset type must be 'image' or 'video'." };
                }
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

    private static MediaAiSupportResult ValidateMediaAiSupported(AiMediaRequest request)
    {
        if (request.MediaItems.Count != 1)
        {
            return MediaAiSupportResult.Unsupported("Media AI requires exactly one media item.");
        }

        var mediaItem = request.MediaItems[0];
        if (mediaItem.AssetType.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Action == AiMediaAction.VideoCaptionIdeas || request.Action == AiMediaAction.ThumbnailSuggest)
            {
                return MediaAiSupportResult.DisabledVideo(mediaItem);
            }

            return MediaAiSupportResult.Unsupported("Media AI does not support video media.");
        }

        if (!mediaItem.AssetType.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            return MediaAiSupportResult.Unsupported("Media AI only supports image media.");
        }

        if (request.Action == AiMediaAction.VideoCaptionIdeas || request.Action == AiMediaAction.ThumbnailSuggest)
        {
            return MediaAiSupportResult.Unsupported("Video AI actions are disabled.");
        }

        return MediaAiSupportResult.Supported(mediaItem);
    }

    private static AiMediaResponseBase CreateDisabledVideoResponse(AiMediaAction action)
    {
        return action switch
        {
            AiMediaAction.VideoCaptionIdeas => new AiMediaCaptionIdeasResponse(action, new List<AiMediaCaptionVariant>()),
            AiMediaAction.ThumbnailSuggest => new AiThumbnailSuggestResponse(action, new List<AiVideoFrame>()),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Action is not a disabled video action.")
        };
    }

    private sealed record MediaAiSupportResult(
        bool IsSupported,
        bool ReturnDisabledVideoResponse,
        AiMediaItemReference? MediaItem,
        string Reason)
    {
        public static MediaAiSupportResult Supported(AiMediaItemReference mediaItem)
            => new(true, false, mediaItem, string.Empty);

        public static MediaAiSupportResult DisabledVideo(AiMediaItemReference mediaItem)
            => new(false, true, mediaItem, "Video AI is disabled.");

        public static MediaAiSupportResult Unsupported(string reason)
            => new(false, false, null, reason);
    }
}
