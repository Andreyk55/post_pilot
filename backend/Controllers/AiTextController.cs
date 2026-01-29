using Microsoft.AspNetCore.Mvc;
using PostPilot.Api.DTOs;
using PostPilot.Api.Services.Ai;

namespace PostPilot.Api.Controllers;

[ApiController]
[Route("api/ai")]
public class AiTextController : ControllerBase
{
    private readonly IGeminiClient _geminiClient;
    private readonly IAiRateLimiter _rateLimiter;
    private readonly ILogger<AiTextController> _logger;

    // TODO: Replace with real user authentication
    private static readonly Guid CurrentUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private const int MinTextLength = 1;
    private const int MaxTextLength = 5000;

    public AiTextController(
        IGeminiClient geminiClient,
        IAiRateLimiter rateLimiter,
        ILogger<AiTextController> logger)
    {
        _geminiClient = geminiClient;
        _rateLimiter = rateLimiter;
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
        // Validate request
        var validationErrors = ValidateRequest(request);
        if (validationErrors.Count > 0)
        {
            return ValidationProblem(new ValidationProblemDetails(validationErrors));
        }

        // Check rate limit
        var canProceed = await _rateLimiter.TryAcquireAsync(CurrentUserId, cancellationToken);
        if (!canProceed)
        {
            var remaining = await _rateLimiter.GetRemainingCallsAsync(CurrentUserId, cancellationToken);
            _logger.LogWarning("Rate limit exceeded for user {UserId}", CurrentUserId);

            return Problem(
                title: "Rate limit exceeded",
                detail: "You've reached the maximum number of AI requests for today. Please try again tomorrow.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        try
        {
            return request.Action switch
            {
                AiTextAction.Polish or AiTextAction.RewriteTone or AiTextAction.Shorten or AiTextAction.Expand =>
                    Ok(await _geminiClient.GenerateVariantsAsync(
                        request.Action,
                        request.Platform,
                        request.Text,
                        request.Tone,
                        request.Language,
                        cancellationToken)),

                AiTextAction.Hashtags =>
                    Ok(await _geminiClient.GenerateHashtagsAsync(
                        request.Platform,
                        request.Text,
                        request.Language,
                        cancellationToken)),

                AiTextAction.PreFlight =>
                    Ok(await _geminiClient.RunPreFlightCheckAsync(
                        request.Platform,
                        request.Text,
                        request.Language,
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
}
