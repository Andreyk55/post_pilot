namespace PostPilot.Api.Services.Ai;

/// <summary>
/// Exception thrown when the Google AI API (Gemini/Gemma) returns an error.
/// </summary>
public class GeminiApiException : Exception
{
    /// <summary>
    /// The HTTP status code associated with the error.
    /// </summary>
    public int StatusCode { get; }

    public GeminiApiException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
