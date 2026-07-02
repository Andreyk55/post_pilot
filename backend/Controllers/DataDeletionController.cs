using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.DataDeletion;
using PostPilot.Api.Settings;

namespace PostPilot.Api.Controllers;

/// <summary>
/// Public endpoints for Meta's Data Deletion Callback and the deletion status page.
/// No PostPilot authentication: Meta's servers call the callback, and end-users open
/// the status URL. Trust comes solely from HMAC-verifying the signed_request — nothing
/// in the request body (userId/workspaceId/pageId) is ever trusted.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class DataDeletionController : ControllerBase
{
    // Fallback when Auth:FrontendUrl is not configured (matches the public deployment).
    private const string DefaultFrontendBaseUrl = "https://www.publishharbor.com";

    private readonly IMetaSignedRequestVerifier _verifier;
    private readonly IDataDeletionRequestService _requests;
    private readonly IMetaDataDeletionService _metaDeletion;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<DataDeletionController> _logger;

    public DataDeletionController(
        IMetaSignedRequestVerifier verifier,
        IDataDeletionRequestService requests,
        IMetaDataDeletionService metaDeletion,
        AuthOptions authOptions,
        ILogger<DataDeletionController> logger)
    {
        _verifier = verifier;
        _requests = requests;
        _metaDeletion = metaDeletion;
        _authOptions = authOptions;
        _logger = logger;
    }

    /// <summary>
    /// Meta Data Deletion Callback. Verifies signed_request, purges the matched Meta
    /// data, and returns the confirmation handle + status URL.
    /// </summary>
    [HttpPost("api/meta/data-deletion")]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> MetaDataDeletion(
        [FromForm(Name = "signed_request")] string? signedRequest,
        CancellationToken ct)
    {
        MetaSignedRequestPayload payload;
        try
        {
            payload = _verifier.Verify(signedRequest!);
        }
        catch (InvalidSignedRequestException ex)
        {
            // Unverified request — delete NOTHING and create NO success status.
            _logger.LogWarning("Rejected Meta data-deletion callback: {Reason}", ex.Message);
            return BadRequest(new { error = "invalid_signed_request" });
        }

        var request = await _requests.CreateProcessingAsync(ProviderType.Meta, payload.UserId, ct);

        try
        {
            var result = await _metaDeletion.PurgeByProviderAccountIdAsync(payload.UserId, ct);

            if (result.Status == DataDeletionStatus.AlreadyDeleted)
            {
                // No matching connection/data — already gone is a success.
                await _requests.MarkAlreadyDeletedAsync(request.ConfirmationCode, ct);
            }
            else
            {
                var warning = result.Warnings.Count > 0 ? string.Join("; ", result.Warnings) : null;
                await _requests.MarkCompletedAsync(
                    request.ConfirmationCode, result.UserId, result.WorkspaceId, warning, ct);
            }
        }
        catch (Exception ex)
        {
            // Unexpected failure: record it (safe message) but still hand Meta a
            // confirmation code so the user can track status. Never leak internals.
            _logger.LogError(ex, "Meta data-deletion purge failed for request {Code}", request.ConfirmationCode);
            await _requests.MarkFailedAsync(request.ConfirmationCode, "Deletion failed; please contact support.", ct);
        }

        var url = $"{FrontendBaseUrl()}/data-deletion/status/{request.ConfirmationCode}";
        return Ok(new MetaDataDeletionCallbackResponse(url, request.ConfirmationCode));
    }

    /// <summary>Public status of a deletion request by its confirmation code.</summary>
    [HttpGet("api/data-deletion/status/{confirmationCode}")]
    public async Task<IActionResult> GetStatus(string confirmationCode, CancellationToken ct)
    {
        var status = await _requests.GetStatusAsync(confirmationCode, ct);
        if (status is null)
        {
            return NotFound(new { error = "not_found" });
        }

        return Ok(new DataDeletionStatusResponse(
            status.ConfirmationCode,
            status.Provider,
            status.Status,
            status.RequestedAt,
            status.CompletedAt));
    }

    private string FrontendBaseUrl()
    {
        var configured = _authOptions.FrontendUrl;
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? DefaultFrontendBaseUrl : configured;
        return baseUrl.TrimEnd('/');
    }
}
