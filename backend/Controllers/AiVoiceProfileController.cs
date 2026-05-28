using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Services.Auth;

namespace PostPilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/ai/voice-profiles")]
public class AiVoiceProfileController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly ILogger<AiVoiceProfileController> _logger;

    private const int MaxNameLength = ValidationLimits.VoiceProfileNameMaxLength;
    private const int MaxDescriptionLength = ValidationLimits.VoiceProfileDescriptionMaxLength;
    private const int MaxRulesLength = ValidationLimits.VoiceProfileDoRulesMaxLength;
    private const int MaxBannedWordsLength = ValidationLimits.VoiceProfileBannedWordsMaxLength;
    private const int MaxExamplePostsLength = ValidationLimits.VoiceProfileExamplePostsMaxLength;

    public AiVoiceProfileController(
        AppDbContext db,
        ICurrentUserProvider currentUser,
        ICurrentWorkspaceProvider currentWorkspace,
        ILogger<AiVoiceProfileController> logger)
    {
        _db = db;
        _currentUser = currentUser;
        _currentWorkspace = currentWorkspace;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<VoiceProfileSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<VoiceProfileSummary>>> GetProfiles(CancellationToken cancellationToken)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profiles = await _db.AiVoiceProfiles
            .Where(p => p.WorkspaceId == workspaceId && !p.IsDeleted)
            .OrderBy(p => p.Name)
            .Select(p => new VoiceProfileSummary(p.Id, p.Name, p.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(profiles);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(VoiceProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VoiceProfileResponse>> GetProfile(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId && !p.IsDeleted, cancellationToken);

        if (profile == null)
        {
            return NotFound();
        }

        return Ok(MapToResponse(profile));
    }

    [HttpPost]
    [ProducesResponseType(typeof(VoiceProfileResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<VoiceProfileResponse>> CreateProfile(
        [FromBody] CreateVoiceProfileRequest request,
        CancellationToken cancellationToken)
    {
        var errors = ValidateRequest(request.Name, request.Description, request.DoRules,
            request.DontRules, request.BannedWords, request.ExamplePosts);

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var userId = _currentUser.GetCurrentUserId();

        var profile = new AiVoiceProfile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            DoRules = request.DoRules?.Trim(),
            DontRules = request.DontRules?.Trim(),
            BannedWords = request.BannedWords?.Trim(),
            ExamplePosts = request.ExamplePosts?.Trim(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.AiVoiceProfiles.Add(profile);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created voice profile {ProfileId} in workspace {WorkspaceId}", profile.Id, workspaceId);

        return CreatedAtAction(nameof(GetProfile), new { id = profile.Id }, MapToResponse(profile));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(VoiceProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VoiceProfileResponse>> UpdateProfile(
        Guid id,
        [FromBody] UpdateVoiceProfileRequest request,
        CancellationToken cancellationToken)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId && !p.IsDeleted, cancellationToken);

        if (profile == null)
        {
            return NotFound();
        }

        var errors = ValidateRequest(request.Name, request.Description, request.DoRules,
            request.DontRules, request.BannedWords, request.ExamplePosts);

        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        profile.Name = request.Name.Trim();
        profile.Description = request.Description?.Trim();
        profile.DoRules = request.DoRules?.Trim();
        profile.DontRules = request.DontRules?.Trim();
        profile.BannedWords = request.BannedWords?.Trim();
        profile.ExamplePosts = request.ExamplePosts?.Trim();
        profile.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated voice profile {ProfileId} in workspace {WorkspaceId}", profile.Id, workspaceId);

        return Ok(MapToResponse(profile));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteProfile(Guid id, CancellationToken cancellationToken)
    {
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);
        var profile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(p => p.Id == id && p.WorkspaceId == workspaceId && !p.IsDeleted, cancellationToken);

        if (profile == null)
        {
            return NotFound();
        }

        if (await IsVoiceProfileInUseAsync(id, workspaceId, cancellationToken))
        {
            return Conflict(new
            {
                error = "VOICE_PROFILE_IN_USE",
                message = "This voice profile is used by one or more posts/settings. Remove it first."
            });
        }

        profile.IsDeleted = true;
        profile.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Soft deleted voice profile {ProfileId} in workspace {WorkspaceId}", profile.Id, workspaceId);

        return NoContent();
    }

    private static VoiceProfileResponse MapToResponse(AiVoiceProfile profile) =>
        new(
            profile.Id,
            profile.Name,
            profile.Description,
            profile.DoRules,
            profile.DontRules,
            profile.BannedWords,
            profile.ExamplePosts,
            profile.CreatedAt,
            profile.UpdatedAt
        );

    private Task<bool> IsVoiceProfileInUseAsync(Guid profileId, Guid workspaceId, CancellationToken cancellationToken)
    {
        // voiceProfileId is currently only used in transient AI requests, so
        // profiles are never "in use". This is intentionally a no-op until we
        // start persisting voiceProfileId on posts/drafts/settings.
        return Task.FromResult(false);
    }

    private static Dictionary<string, string[]> ValidateRequest(
        string name,
        string? description,
        string? doRules,
        string? dontRules,
        string? bannedWords,
        string? examplePosts)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Name is required."];
        }
        else if (name.Length < ValidationLimits.VoiceProfileNameMinLength)
        {
            errors["name"] = [$"Name must be at least {ValidationLimits.VoiceProfileNameMinLength} characters."];
        }
        else if (name.Length > ValidationLimits.VoiceProfileNameMaxLength)
        {
            errors["name"] = [$"Name must not exceed {ValidationLimits.VoiceProfileNameMaxLength} characters."];
        }

        if (description?.Length > ValidationLimits.VoiceProfileDescriptionMaxLength)
        {
            errors["description"] = [$"Description must not exceed {ValidationLimits.VoiceProfileDescriptionMaxLength} characters."];
        }

        if (doRules?.Length > ValidationLimits.VoiceProfileDoRulesMaxLength)
        {
            errors["doRules"] = [$"Do rules must not exceed {ValidationLimits.VoiceProfileDoRulesMaxLength} characters."];
        }

        if (dontRules?.Length > ValidationLimits.VoiceProfileDontRulesMaxLength)
        {
            errors["dontRules"] = [$"Don't rules must not exceed {ValidationLimits.VoiceProfileDontRulesMaxLength} characters."];
        }

        if (bannedWords?.Length > ValidationLimits.VoiceProfileBannedWordsMaxLength)
        {
            errors["bannedWords"] = [$"Banned words must not exceed {ValidationLimits.VoiceProfileBannedWordsMaxLength} characters."];
        }

        if (examplePosts?.Length > ValidationLimits.VoiceProfileExamplePostsMaxLength)
        {
            errors["examplePosts"] = [$"Example posts must not exceed {ValidationLimits.VoiceProfileExamplePostsMaxLength} characters."];
        }

        var totalLength = (name?.Length ?? 0) +
                          (description?.Length ?? 0) +
                          (doRules?.Length ?? 0) +
                          (dontRules?.Length ?? 0) +
                          (bannedWords?.Length ?? 0) +
                          (examplePosts?.Length ?? 0);

        if (totalLength > ValidationLimits.VoiceProfileTotalMaxLength)
        {
            errors["total"] = [$"Total voice profile content must not exceed {ValidationLimits.VoiceProfileTotalMaxLength} characters."];
        }

        return errors;
    }
}
