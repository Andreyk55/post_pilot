using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/meta")]
public class MetaController : ControllerBase
{
    private readonly IMetaOAuthService _metaOAuthService;
    private readonly ILogger<MetaController> _logger;

    // TODO: Replace with actual user authentication
    private static readonly Guid CurrentUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public MetaController(IMetaOAuthService metaOAuthService, ILogger<MetaController> logger)
    {
        _metaOAuthService = metaOAuthService;
        _logger = logger;
    }

    /// <summary>
    /// Start Meta OAuth flow - returns authorization URL
    /// </summary>
    [HttpPost("oauth/start")]
    public async Task<ActionResult<MetaOAuthStartResponse>> StartOAuth()
    {
        try
        {
            var result = await _metaOAuthService.StartOAuthAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Meta OAuth");
            return StatusCode(500, new { error = "Failed to start OAuth flow" });
        }
    }

    /// <summary>
    /// Handle OAuth callback - exchange code for token and return available pages
    /// </summary>
    [HttpPost("oauth/callback")]
    public async Task<ActionResult<MetaOAuthCallbackResponse>> HandleCallback([FromBody] MetaOAuthCallbackRequest request)
    {
        try
        {
            var result = await _metaOAuthService.HandleCallbackAsync(request.Code, request.State);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "OAuth callback failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle Meta OAuth callback");
            return StatusCode(500, new { error = "Failed to complete OAuth" });
        }
    }

    /// <summary>
    /// Complete OAuth and save connection immediately (identity-level only, no page selection)
    /// </summary>
    [HttpPost("oauth/complete")]
    public async Task<ActionResult<MetaOAuthCompleteResponse>> CompleteOAuth([FromBody] MetaOAuthCompleteRequest request)
    {
        try
        {
            var result = await _metaOAuthService.CompleteOAuthAsync(request.Code, request.State);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "OAuth complete failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete Meta OAuth");
            return StatusCode(500, new { error = "Failed to complete OAuth" });
        }
    }

    /// <summary>
    /// Discover Instagram Business accounts linked to selected pages
    /// </summary>
    [HttpPost("instagram/discover")]
    public async Task<ActionResult<MetaDiscoverInstagramResponse>> DiscoverInstagram([FromBody] MetaDiscoverInstagramRequest request)
    {
        try
        {
            var result = await _metaOAuthService.DiscoverInstagramAccountsAsync(request.TempToken, request.PageIds);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Instagram discovery failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover Instagram accounts");
            return StatusCode(500, new { error = "Failed to discover Instagram accounts" });
        }
    }

    /// <summary>
    /// Save Meta connection with selected pages and Instagram accounts
    /// </summary>
    [HttpPost("connection")]
    public async Task<ActionResult<MetaSaveConnectionResponse>> SaveConnection([FromBody] MetaSaveConnectionRequest request)
    {
        try
        {
            var result = await _metaOAuthService.SaveConnectionAsync(
                request.TempToken,
                request.SelectedPageIds,
                request.SelectedInstagramIds
            );
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Save connection failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Meta connection");
            return StatusCode(500, new { error = "Failed to save connection" });
        }
    }

    /// <summary>
    /// Get current Meta connection status
    /// </summary>
    [HttpGet("connection")]
    public async Task<ActionResult<MetaConnectionResponse>> GetConnection()
    {
        try
        {
            var result = await _metaOAuthService.GetConnectionAsync(CurrentUserId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Meta connection");
            return StatusCode(500, new { error = "Failed to get connection" });
        }
    }

    /// <summary>
    /// Get available pages (for manage flow)
    /// </summary>
    [HttpGet("pages")]
    public async Task<ActionResult<MetaAvailablePagesResponse>> GetAvailablePages()
    {
        try
        {
            var result = await _metaOAuthService.GetAvailablePagesAsync(CurrentUserId);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Get pages failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available pages");
            return StatusCode(500, new { error = "Failed to get pages" });
        }
    }

    /// <summary>
    /// Update selected pages and Instagram accounts (manage flow)
    /// </summary>
    [HttpPut("connection")]
    public async Task<ActionResult<MetaSaveConnectionResponse>> UpdateConnection([FromBody] MetaUpdatePagesRequest request)
    {
        try
        {
            var result = await _metaOAuthService.UpdateConnectionAsync(
                CurrentUserId,
                request.SelectedPageIds,
                request.SelectedInstagramIds
            );
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Update connection failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Meta connection");
            return StatusCode(500, new { error = "Failed to update connection" });
        }
    }

    /// <summary>
    /// Disconnect Meta - revoke tokens and remove connection
    /// </summary>
    [HttpDelete("connection")]
    public async Task<IActionResult> Disconnect()
    {
        try
        {
            await _metaOAuthService.DisconnectAsync(CurrentUserId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Meta");
            return StatusCode(500, new { error = "Failed to disconnect" });
        }
    }

    /// <summary>
    /// Get validation limits for the application
    /// </summary>
    [HttpGet("limits")]
    public ActionResult<ValidationLimitsResponse> GetLimits()
    {
        return Ok(new ValidationLimitsResponse(
            new VoiceProfileLimits(
                ValidationLimits.VoiceProfileNameMinLength,
                ValidationLimits.VoiceProfileNameMaxLength,
                ValidationLimits.VoiceProfileDescriptionMaxLength,
                ValidationLimits.VoiceProfileDoRulesMaxLength,
                ValidationLimits.VoiceProfileDontRulesMaxLength,
                ValidationLimits.VoiceProfileBannedWordsMaxLength,
                ValidationLimits.VoiceProfileExamplePostsMaxLength,
                ValidationLimits.VoiceProfileTotalMaxLength
            ),
            new PostLimits(
                ValidationLimits.PostTextMaxLength,
                ValidationLimits.PostTitleMaxLength,
                ValidationLimits.PostMaxHashtags,
                ValidationLimits.PostMaxMediaFiles
            ),
            new MediaLimits(
                ValidationLimits.MediaImageMaxBytes,
                ValidationLimits.MediaVideoMaxBytes
            )
        ));
    }
}
