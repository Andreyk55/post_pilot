namespace PostPilot.Api.Services.Support;

/// <summary>
/// Thrown by <see cref="ISupportContactService"/> when the submitted subject/message/category
/// is invalid. Carries a field → messages map so the controller can return a standard
/// <c>ValidationProblemDetails</c> (HTTP 400). Maps to HTTP 400 Bad Request.
/// </summary>
public sealed class SupportValidationException : Exception
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public SupportValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Support request validation failed.")
    {
        Errors = errors;
    }
}

/// <summary>
/// Thrown when the authenticated user has exceeded the per-user support-message cap within
/// the rolling window. Maps to HTTP 429 Too Many Requests.
/// </summary>
public sealed class SupportRateLimitExceededException : Exception
{
    public SupportRateLimitExceededException(string message) : base(message) { }
}
