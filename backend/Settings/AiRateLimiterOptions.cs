namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for the AI rate limiter.
/// Bound from "Ai:RateLimiter" config section. All defaults in appsettings.common.json.
/// </summary>
public class AiRateLimiterOptions
{
    /// <summary>
    /// Max AI calls allowed per user within the window.
    /// </summary>
    public int MaxCallsPerDay { get; set; }

    /// <summary>
    /// Window size in hours for rate limiting.
    /// </summary>
    public int WindowHours { get; set; }
}
