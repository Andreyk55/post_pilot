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

    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 1000;
    private const int MaxRulesLength = 2000;
    private const int MaxBannedWordsLength = 1000;
    private const int MaxExamplePostsLength = 5000;

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
            .Where(p => p.UserId == CurrentUserId)
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
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == CurrentUserId, cancellationToken);

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
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == CurrentUserId, cancellationToken);

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
    public async Task<IActionResult> DeleteProfile(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _db.AiVoiceProfiles
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == CurrentUserId, cancellationToken);

        if (profile == null)
        {
            return NotFound();
        }

        _db.AiVoiceProfiles.Remove(profile);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deleted voice profile {ProfileId} for user {UserId}", profile.Id, CurrentUserId);

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
        else if (name.Length > MaxNameLength)
        {
            errors["name"] = [$"Name must not exceed {MaxNameLength} characters."];
        }

        if (description?.Length > MaxDescriptionLength)
        {
            errors["description"] = [$"Description must not exceed {MaxDescriptionLength} characters."];
        }

        if (doRules?.Length > MaxRulesLength)
        {
            errors["doRules"] = [$"Do rules must not exceed {MaxRulesLength} characters."];
        }

        if (dontRules?.Length > MaxRulesLength)
        {
            errors["dontRules"] = [$"Don't rules must not exceed {MaxRulesLength} characters."];
        }

        if (bannedWords?.Length > MaxBannedWordsLength)
        {
            errors["bannedWords"] = [$"Banned words must not exceed {MaxBannedWordsLength} characters."];
        }

        if (examplePosts?.Length > MaxExamplePostsLength)
        {
            errors["examplePosts"] = [$"Example posts must not exceed {MaxExamplePostsLength} characters."];
        }

        return errors;
    }
}
