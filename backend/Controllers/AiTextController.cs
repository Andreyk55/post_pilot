using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Auth;

namespace PostPilot.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/ai")]
public class AiTextController : ControllerBase
{
    private readonly IGeminiClient _geminiClient;
    private readonly IAiRateLimiter _rateLimiter;
    private readonly AppDbContext _db;
    private readonly LanguageService _languageService;
    private readonly CaptionAssistService _captionAssistService;
    private readonly PostTimeSuggestionService _postTimeSuggestionService;
    private readonly ICurrentUserProvider _currentUser;
    private readonly ICurrentWorkspaceProvider _currentWorkspace;
    private readonly ILogger<AiTextController> _logger;

    private const int MinTextLength = 1;
    private const int MaxTextLength = 5000;

    public AiTextController(
        IGeminiClient geminiClient,
        IAiRateLimiter rateLimiter,
        AppDbContext db,
        LanguageService languageService,
        CaptionAssistService captionAssistService,
        PostTimeSuggestionService postTimeSuggestionService,
        ICurrentUserProvider currentUser,
        ICurrentWorkspaceProvider currentWorkspace,
        ILogger<AiTextController> logger)
    {
        _geminiClient = geminiClient;
        _rateLimiter = rateLimiter;
        _db = db;
        _languageService = languageService;
        _captionAssistService = captionAssistService;
        _postTimeSuggestionService = postTimeSuggestionService;
        _currentUser = currentUser;
        _currentWorkspace = currentWorkspace;
        _logger = logger;
    }

    /// <summary>
    /// Process text with AI assistance (polish, rewrite, shorten, expand, hashtags, or pre-flight check).
    /// </summary>
    [HttpPost("text")]
    [ProducesResponseType(typeof(AiTextVariantsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AiHashtagsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AiPreFlightResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ProcessText(
        [FromBody] AiTextRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetCurrentUserId();
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);

        // Validate request
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // Check rate limit
        var canProceed = await _rateLimiter.TryAcquireAsync(userId, cancellationToken);
        if (!canProceed)
        {
            var remaining = await _rateLimiter.GetRemainingCallsAsync(userId, cancellationToken);
            _logger.LogWarning("Rate limit exceeded for user {UserId}", userId);

            return Problem(
                title: "Rate limit exceeded",
                detail: "You've reached the maximum number of AI requests for today. Please try again tomorrow.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            // Load voice profile if provided
            AiVoiceProfile? voiceProfile = null;
            if (request.VoiceProfileId.HasValue)
            {
                voiceProfile = await _db.AiVoiceProfiles
                    .FirstOrDefaultAsync(p => p.Id == request.VoiceProfileId.Value && p.WorkspaceId == workspaceId && !p.IsDeleted, cancellationToken);

                if (voiceProfile == null)
                {
                    _logger.LogWarning("Voice profile {ProfileId} not found for user {UserId}", request.VoiceProfileId, userId);
                    // Continue without voice profile rather than failing
                }
            }

            return request.Action switch
            {
                AiTextAction.Polish or AiTextAction.RewriteTone or AiTextAction.Shorten or AiTextAction.Expand =>
                    Ok(await _geminiClient.GenerateVariantsAsync(
                        request.Action,
                        request.Platform,
                        request.Text,
                        request.Tone,
                        request.Language,
                        voiceProfile,
                        cancellationToken)),

                AiTextAction.Hashtags =>
                    Ok(await _geminiClient.GenerateHashtagsAsync(
                        request.Platform,
                        request.Text,
                        request.Language,
                        voiceProfile,
                        cancellationToken)),

                AiTextAction.PreFlight =>
                    Ok(await _geminiClient.RunPreFlightCheckAsync(
                        request.Platform,
                        request.Text,
                        request.Language,
                        voiceProfile,
                        cancellationToken)),

                _ => BadRequest(new { error = $"Unknown action: {request.Action}" })
            };
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            return Problem(
                title: "AI service quota exceeded",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing AI text request");

            return Problem(
                title: "Internal error",
                detail: "An unexpected error occurred. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Generate text variants with full control options (goal, tone, length, include flags).
    /// </summary>
    [HttpPost("text/generate")]
    [ProducesResponseType(typeof(AiGenerateVariantsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GenerateVariants(
        [FromBody] AiGenerateVariantsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetCurrentUserId();
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);

        // Validate request
        var validationErrors = ValidateGenerateRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // Check rate limit
        var canProceed = await _rateLimiter.TryAcquireAsync(userId, cancellationToken);
        if (!canProceed)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}", userId);

            return Problem(
                title: "Rate limit exceeded",
                detail: "You've reached the maximum number of AI requests for today. Please try again tomorrow.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            // Load voice profile if provided
            AiVoiceProfile? voiceProfile = null;
            if (request.VoiceProfileId.HasValue)
            {
                voiceProfile = await _db.AiVoiceProfiles
                    .FirstOrDefaultAsync(p => p.Id == request.VoiceProfileId.Value && p.WorkspaceId == workspaceId && !p.IsDeleted, cancellationToken);

                if (voiceProfile == null)
                {
                    _logger.LogWarning("Voice profile {ProfileId} not found for user {UserId}", request.VoiceProfileId, userId);
                }
            }

            var result = await _geminiClient.GenerateCreatorVariantsAsync(request, voiceProfile, cancellationToken);
            return Ok(result);
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            return Problem(
                title: "AI service quota exceeded",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error generating AI variants");

            return Problem(
                title: "Internal error",
                detail: "An unexpected error occurred. Please try again.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Dictionary<string, string[]> ValidateGenerateRequest(AiGenerateVariantsRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.InputText))
        {
            errors["inputText"] = new[] { "Input text is required." };
        }
        else if (request.InputText.Length < MinTextLength)
        {
            errors["inputText"] = new[] { $"Input text must be at least {MinTextLength} character(s)." };
        }
        else if (request.InputText.Length > MaxTextLength)
        {
            errors["inputText"] = new[] { $"Input text must not exceed {MaxTextLength} characters." };
        }

        if (!Enum.IsDefined(request.Platform))
        {
            errors["platform"] = new[] { "Invalid platform value." };
        }

        if (!Enum.IsDefined(request.Goal))
        {
            errors["goal"] = new[] { "Invalid goal value." };
        }

        if (!Enum.IsDefined(request.Tone))
        {
            errors["tone"] = new[] { "Invalid tone value." };
        }

        if (!Enum.IsDefined(request.Length))
        {
            errors["length"] = new[] { "Invalid length value." };
        }

        if (request.NumVariants < 1 || request.NumVariants > 5)
        {
            errors["numVariants"] = new[] { "Number of variants must be between 1 and 5." };
        }

        // RegenerateIndex is the position in the UI list to replace, not related to numVariants.
        // When regenerating, numVariants indicates how many new variants to generate (usually 1),
        // while regenerateIndex indicates which position in the original list to replace.
        if (request.RegenerateIndex.HasValue && request.RegenerateIndex < 0)
        {
            errors["regenerateIndex"] = new[] { "Regenerate index cannot be negative." };
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateRequest(AiTextRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            errors["text"] = new[] { "Text is required." };
        }
        else if (request.Text.Length < MinTextLength)
        {
            errors["text"] = new[] { $"Text must be at least {MinTextLength} character(s)." };
        }
        else if (request.Text.Length > MaxTextLength)
        {
            errors["text"] = new[] { $"Text must not exceed {MaxTextLength} characters." };
        }

        if (request.Action == AiTextAction.RewriteTone && !request.Tone.HasValue)
        {
            errors["tone"] = new[] { "Tone is required for RewriteTone action." };
        }

        if (!Enum.IsDefined(request.Action))
        {
            errors["action"] = new[] { "Invalid action value." };
        }

        if (!Enum.IsDefined(request.Platform))
        {
            errors["platform"] = new[] { "Invalid platform value." };
        }

        return errors;
    }

    /// <summary>
    /// Detect the language of the given text.
    /// </summary>
    [HttpPost("language/detect")]
    [ProducesResponseType(typeof(LanguageDetectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DetectLanguage(
        [FromBody] LanguageDetectRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Errors = { ["text"] = new[] { "Text is required." } }
            });
        }

        if (request.Text.Length > MaxTextLength)
        {
            return ValidationProblem(new ValidationProblemDetails
            {
                Errors = { ["text"] = new[] { $"Text must not exceed {MaxTextLength} characters." } }
            });
        }

        try
        {
            var result = await _languageService.DetectLanguageAsync(request.Text, cancellationToken);
            return Ok(new LanguageDetectResponse(result.LanguageCode, result.Confidence, result.IsReliable));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting language");
            return Problem(
                title: "Language detection failed",
                detail: "An error occurred while detecting the language.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Generate multilingual captions with translation or rewriting support.
    /// </summary>
    [HttpPost("captions/generate")]
    [ProducesResponseType(typeof(CaptionGenerateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GenerateCaptions(
        [FromBody] DTOs.CaptionGenerateRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetCurrentUserId();
        var workspaceId = await _currentWorkspace.GetCurrentWorkspaceIdAsync(cancellationToken);

        // Validate request
        var errors = ValidateCaptionRequest(request);
        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        // Check rate limit
        var canProceed = await _rateLimiter.TryAcquireAsync(userId, cancellationToken);
        if (!canProceed)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}", userId);
            return Problem(
                title: "Rate limit exceeded",
                detail: "You've reached the maximum number of AI requests for today. Please try again tomorrow.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            // Load voice profile if provided
            AiVoiceProfile? voiceProfile = null;
            if (request.VoiceProfileId.HasValue)
            {
                voiceProfile = await _db.AiVoiceProfiles
                    .FirstOrDefaultAsync(p => p.Id == request.VoiceProfileId.Value && p.WorkspaceId == workspaceId && !p.IsDeleted, cancellationToken);

                if (voiceProfile == null)
                {
                    _logger.LogWarning("Voice profile {ProfileId} not found for user {UserId}", request.VoiceProfileId, userId);
                    // Continue without voice profile
                }
            }

            // Generate captions
            var (detection, captions, warnings) = await _captionAssistService.GenerateCaptionsAsync(
                request.Text,
                request.Platform,
                request.OutputLanguage,
                request.Variants,
                request.StrictMeaning,
                request.KeepBrandVoice,
                voiceProfile,
                request.SourceLanguage,
                cancellationToken);

            return Ok(new CaptionGenerateResponse(
                detection.LanguageCode,
                detection.Confidence,
                detection.IsReliable,
                string.IsNullOrWhiteSpace(request.OutputLanguage) || request.OutputLanguage == "auto"
                    ? detection.LanguageCode
                    : request.OutputLanguage,
                captions.ToList(),
                warnings.ToList()));
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            return Problem(
                title: "AI service quota exceeded",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating captions");
            return Problem(
                title: "Caption generation failed",
                detail: "An error occurred while generating captions.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Dictionary<string, string[]> ValidateCaptionRequest(DTOs.CaptionGenerateRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            errors["text"] = new[] { "Text is required." };
        }
        else if (request.Text.Length < MinTextLength)
        {
            errors["text"] = new[] { $"Text must be at least {MinTextLength} character(s)." };
        }
        else if (request.Text.Length > MaxTextLength)
        {
            errors["text"] = new[] { $"Text must not exceed {MaxTextLength} characters." };
        }

        if (!Enum.IsDefined(request.Platform))
        {
            errors["platform"] = new[] { "Invalid platform value." };
        }

        if (request.Variants < 1 || request.Variants > 3)
        {
            errors["variants"] = new[] { "Number of variants must be between 1 and 3." };
        }

        return errors;
    }

    /// <summary>
    /// Get AI-powered suggestions for optimal posting times.
    /// </summary>
    [HttpPost("suggest-post-time")]
    [ProducesResponseType(typeof(PostTimeSuggestionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SuggestPostTime(
        [FromBody] PostTimeSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = _currentUser.GetCurrentUserId();

        // Validate request
        var errors = ValidatePostTimeRequest(request);
        if (errors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(errors));
        }

        // Check rate limit
        var canProceed = await _rateLimiter.TryAcquireAsync(userId, cancellationToken);
        if (!canProceed)
        {
            _logger.LogWarning("Rate limit exceeded for user {UserId}", userId);
            return Problem(
                title: "Rate limit exceeded",
                detail: "You've reached the maximum number of AI requests for today. Please try again tomorrow.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            var result = await _postTimeSuggestionService.SuggestPostTimeAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (GeminiApiException ex) when (ex.StatusCode == 429)
        {
            return Problem(
                title: "AI service quota exceeded",
                detail: ex.Message,
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        catch (Exception ex)
        {
            // Log but don't fail - service has fallback
            _logger.LogWarning(ex, "Error getting AI time suggestion, returning fallback");
            var result = await _postTimeSuggestionService.SuggestPostTimeAsync(request, cancellationToken);
            return Ok(result);
        }
    }

    private static Dictionary<string, string[]> ValidatePostTimeRequest(PostTimeSuggestionRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.PostText))
        {
            errors["postText"] = new[] { "Post text is required." };
        }
        else if (request.PostText.Length > MaxTextLength)
        {
            errors["postText"] = new[] { $"Post text must not exceed {MaxTextLength} characters." };
        }

        if (!Enum.IsDefined(request.Platform))
        {
            errors["platform"] = new[] { "Invalid platform value." };
        }

        if (!Enum.IsDefined(request.Goal))
        {
            errors["goal"] = new[] { "Invalid goal value." };
        }

        if (string.IsNullOrWhiteSpace(request.Weekday))
        {
            errors["weekday"] = new[] { "Weekday is required." };
        }

        if (string.IsNullOrWhiteSpace(request.Timezone))
        {
            errors["timezone"] = new[] { "Timezone is required." };
        }

        if (!Enum.IsDefined(request.AudienceLocation))
        {
            errors["audienceLocation"] = new[] { "Invalid audience location value." };
        }

        if (request.AudienceLocation == AudienceLocationMode.SpecificCountry &&
            string.IsNullOrWhiteSpace(request.Country))
        {
            errors["country"] = new[] { "Country is required when audience location is SpecificCountry." };
        }

        return errors;
    }
}
