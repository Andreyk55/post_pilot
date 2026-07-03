using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/meta")]
public class MetaController : ControllerBase
{
    private readonly IMetaOAuthService _metaOAuthService;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly IWebHostEnvironment _env;
    private readonly MetaOptions _metaOptions;
    private readonly ILogger<MetaController> _logger;

    public MetaController(
        IMetaOAuthService metaOAuthService,
        ICurrentUserProvider currentUser,
        ICurrentWorkspaceProvider currentWorkspace,
        IWebHostEnvironment env,
        MetaOptions metaOptions,
        ILogger<MetaController> logger)
    {
        _metaOAuthService = metaOAuthService;
        _currentUser = currentUser;
        _currentWorkspace = currentWorkspace;
        _env = env;
        _metaOptions = metaOptions;
        _logger = logger;
    }

    /// <summary>
    /// Whether the Meta diagnostic endpoints may run: only in Development, or when explicitly
    /// enabled via Meta:EnableDebugEndpoints. Off (→ 404) in production by default.
    /// </summary>
    private bool DebugEndpointsEnabled => _env.IsDevelopment() || _metaOptions.EnableDebugEndpoints;

    [HttpPost("oauth/start")]
    public async Task<ActionResult<MetaOAuthStartResponse>> StartOAuth()
    {
        // Resolve the workspace OUTSIDE the try so workspace-resolution failures
        // (stale/missing → 409, unauthorized → 403) propagate to the global
        // middleware instead of being flattened into a 500 by the catch below.
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.StartOAuthAsync(workspaceId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Meta OAuth");
            return StatusCode(500, new { error = "Failed to start OAuth flow" });
        }
    }

    [HttpPost("oauth/callback")]
    public async Task<ActionResult<MetaOAuthCallbackResponse>> HandleCallback([FromBody] MetaOAuthCallbackRequest request)
    {
        // Resolve the caller's current workspace OUTSIDE the try so membership/selection
        // failures (409/403) surface via the global middleware. This is the workspace the
        // state MUST have been minted for; the service rejects any mismatch.
        var currentWorkspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.HandleCallbackAsync(request.Code, request.State, currentWorkspaceId);
            return Ok(result);
        }
        catch (OAuthStateAccessDeniedException ex)
        {
            // Session binding: the state belongs to a different workspace than the caller's
            // current one. Return 403 with a fixed, non-revealing message.
            _logger.LogWarning("OAuth callback rejected (state workspace mismatch).");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (ProviderAccountMismatchException ex)
        {
            // Permanent binding rule: this workspace is linked to a different provider
            // account. Reject at callback time — UI must NOT open page selection.
            _logger.LogWarning("OAuth callback rejected (account mismatch): {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
        }
        catch (ProviderOwnedByAnotherWorkspaceException ex)
        {
            // Permanent ownership rule: this account belongs to a DIFFERENT workspace.
            // Reject immediately, before fetching pages or returning the selection list.
            _logger.LogWarning("OAuth callback rejected (owned elsewhere): {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
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

    [HttpPost("oauth/complete")]
    public async Task<ActionResult<MetaOAuthCompleteResponse>> CompleteOAuth([FromBody] MetaOAuthCompleteRequest request)
    {
        // Resolve caller identity + current workspace OUTSIDE the try (see HandleCallback).
        var userId = _currentUser.GetCurrentUserId();
        var currentWorkspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.CompleteOAuthAsync(request.Code, request.State, userId, currentWorkspaceId);
            return Ok(result);
        }
        catch (OAuthStateAccessDeniedException ex)
        {
            _logger.LogWarning("OAuth complete rejected (state workspace mismatch).");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (ProviderAccountMismatchException ex)
        {
            // Permanent binding rule: this workspace is linked to a different provider
            // account. UI must tell the user to reconnect the original account.
            _logger.LogWarning("OAuth complete rejected (account mismatch): {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
        }
        catch (ProviderOwnedByAnotherWorkspaceException ex)
        {
            // Generic ownership rule: the social account/page is owned by a DIFFERENT
            // workspace. UI must tell the user to disconnect it there first.
            _logger.LogWarning("OAuth complete rejected (owned elsewhere): {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
        }
        catch (ProviderAlreadyConnectedException ex)
        {
            // Product rule: workspace already has an active connection for this
            // provider. UI must prompt the user to disconnect first.
            _logger.LogWarning("OAuth complete rejected: {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
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

    [HttpPost("instagram/discover")]
    public async Task<ActionResult<MetaDiscoverInstagramResponse>> DiscoverInstagram([FromBody] MetaDiscoverInstagramRequest request)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.DiscoverInstagramAccountsAsync(request.TempToken, request.PageIds, workspaceId);
            return Ok(result);
        }
        catch (OAuthStateAccessDeniedException ex)
        {
            _logger.LogWarning("Instagram discovery rejected (state workspace mismatch).");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
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

    [HttpPost("connection")]
    public async Task<ActionResult<MetaSaveConnectionResponse>> SaveConnection([FromBody] MetaSaveConnectionRequest request)
    {
        // Resolve caller identity + current workspace OUTSIDE the try (see HandleCallback).
        var userId = _currentUser.GetCurrentUserId();
        var currentWorkspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.SaveConnectionAsync(
                request.TempToken,
                request.SelectedPageIds,
                request.SelectedInstagramIds,
                userId,
                currentWorkspaceId
            );
            return Ok(result);
        }
        catch (OAuthStateAccessDeniedException ex)
        {
            _logger.LogWarning("Save connection rejected (state workspace mismatch).");
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (ProviderAccountMismatchException ex)
        {
            _logger.LogWarning("Save connection rejected (account mismatch): {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
        }
        catch (ProviderOwnedByAnotherWorkspaceException ex)
        {
            _logger.LogWarning("Save connection rejected (owned elsewhere): {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
        }
        catch (ProviderAlreadyConnectedException ex)
        {
            _logger.LogWarning("Save connection rejected: {Message}", ex.Message);
            return Conflict(new { error = ex.Message, provider = ex.Provider.ToString() });
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

    [HttpGet("connection")]
    public async Task<ActionResult<MetaConnectionResponse>> GetConnection()
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.GetConnectionAsync(workspaceId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Meta connection");
            return StatusCode(500, new { error = "Failed to get connection" });
        }
    }

    [HttpGet("pages")]
    public async Task<ActionResult<MetaAvailablePagesResponse>> GetAvailablePages()
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.GetAvailablePagesAsync(workspaceId);
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

    [HttpPut("connection")]
    public async Task<ActionResult<MetaSaveConnectionResponse>> UpdateConnection([FromBody] MetaUpdatePagesRequest request)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.UpdateConnectionAsync(
                workspaceId,
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

    [HttpPost("connection/refresh")]
    public async Task<ActionResult<MetaSaveConnectionResponse>> RefreshAssets()
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.RefreshAssetsAsync(workspaceId);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Refresh assets failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Meta assets");
            return StatusCode(500, new { error = "Failed to refresh assets" });
        }
    }

    [HttpDelete("connection")]
    public async Task<IActionResult> Disconnect()
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            await _metaOAuthService.DisconnectAsync(workspaceId);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect Meta");
            return StatusCode(500, new { error = "Failed to disconnect" });
        }
    }

    [HttpGet("instagram/eligibility")]
    public async Task<ActionResult<InstagramDiscoveryResponse>> GetInstagramEligibility()
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.DiscoverInstagramEligibilityAsync(workspaceId);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Instagram eligibility check failed: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check Instagram eligibility");
            return StatusCode(500, new { error = "Failed to check Instagram eligibility" });
        }
    }

    [HttpGet("instagram/debug")]
    public async Task<ActionResult<object>> DebugInstagramDiscovery()
    {
        // Diagnostic endpoint: hidden (404) in production unless explicitly enabled. Checked
        // before resolving the workspace so a disabled endpoint reveals nothing about state.
        if (!DebugEndpointsEnabled)
        {
            return NotFound();
        }

        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync();
        try
        {
            var result = await _metaOAuthService.DebugInstagramDiscoveryAsync(workspaceId);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to debug Instagram discovery");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("limits")]
    [AllowAnonymous]
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
