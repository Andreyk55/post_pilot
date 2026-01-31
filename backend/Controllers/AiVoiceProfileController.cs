using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/ai/voice-profiles")]
public class AiVoiceProfileController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<AiVoiceProfileController> _logger;

    // TODO: Replace with real user authentication
    private static readonly Guid CurrentUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const int MaxNameLength = ValidationLimits.VoiceProfileNameMaxLength;
    private const int MaxDescriptionLength = ValidationLimits.VoiceProfileDescriptionMaxLength;
    private const int MaxRulesLength = ValidationLimits.VoiceProfileDoRulesMaxLength; // Same for Do and Don't
    private const int MaxBannedWordsLength = ValidationLimits.VoiceProfileBannedWordsMaxLength;
    private const int MaxExamplePostsLength = ValidationLimits.VoiceProfileExamplePostsMaxLength;

    public AiVoiceProfileController(AppDbContext db, ILogger<AiVoiceProfileController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Get all voice profiles for the current user.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<VoiceProfileSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<VoiceProfileSummary>>> GetProfiles(CancellationToken cancellationToken)
    {
        var profiles = await _db.AiVoiceProfiles
            .Where(p => p.UserId == CurrentUserId && !p.IsDeleted)
            .OrderBy(p => p.Name)
            .Select(p => new VoiceProfileSummary(p.Id, p.Name, p.UpdatedAt))
            .ToListAsync(cancellationToken);

        return Ok(profiles);
    }

    /// <summary>
    /// Get a specific voice profile by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(VoiceProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VoiceProfileResponse>> GetProfile(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == CurrentUserId && !p.IsDeleted, cancellationToken);

        if (profile == null)
        {
            return NotFound();
        }

        return Ok(MapToResponse(profile));
    }

    /// <summary>
    /// Create a new voice profile.
    /// </summary>
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

        var profile = new AiVoiceProfile
        {
            Id = Guid.NewGuid(),
            UserId = CurrentUserId,
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

        _logger.LogInformation("Created voice profile {ProfileId} for user {UserId}", profile.Id, CurrentUserId);

        return CreatedAtAction(nameof(GetProfile), new { id = profile.Id }, MapToResponse(profile));
    }

    /// <summary>
    /// Update an existing voice profile.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(VoiceProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<VoiceProfileResponse>> UpdateProfile(
        Guid id,
        [FromBody] UpdateVoiceProfileRequest request,
        CancellationToken cancellationToken)
    {
        var profile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == CurrentUserId && !p.IsDeleted, cancellationToken);

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

        _logger.LogInformation("Updated voice profile {ProfileId} for user {UserId}", profile.Id, CurrentUserId);

        return Ok(MapToResponse(profile));
    }

    /// <summary>
    /// Delete a voice profile.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteProfile(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == CurrentUserId && !p.IsDeleted, cancellationToken);

        if (profile == null)
        {
            return NotFound();
        }

        // Check if profile is currently in use
        if (await IsVoiceProfileInUseAsync(id, CurrentUserId, cancellationToken))
        {
            return Conflict(new
            {
                error = "VOICE_PROFILE_IN_USE",
                message = "This voice profile is used by one or more posts/settings. Remove it first."
            });
        }

        // Soft delete the profile
        profile.IsDeleted = true;
        profile.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Soft deleted voice profile {ProfileId} for user {UserId}", profile.Id, CurrentUserId);

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

    private async Task<bool> IsVoiceProfileInUseAsync(Guid profileId, Guid userId, CancellationToken cancellationToken)
    {
        // TODO: Check if voiceProfileId is referenced in:
        // - User settings/preferences (if they exist)
        // - ScheduledPosts (if they store voiceProfileId)
        // - Drafts (if they exist)
        // - Any other persistent storage

        // For now, since voiceProfileId is only used in transient AI requests,
        // it's not persistently stored, so profiles are never "in use"
        return false;
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

        // Check total combined length
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
