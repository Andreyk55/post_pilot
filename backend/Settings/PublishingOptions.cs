namespace PostPilot.Api.Settings;

/// <summary>
/// Configuration options for the post publishing pipeline.
/// Bound from "Publishing" config section.
/// </summary>
public class PublishingOptions
{
    public const string SectionName = "Publishing";

    /// <summary>
    /// Background worker polling interval in seconds.
    /// </summary>
    public int WorkerPollIntervalSeconds { get; set; }

    /// <summary>
    /// Minutes before a post stuck in Publishing status is recovered.
    /// </summary>
    public int StuckPostThresholdMinutes { get; set; }

    /// <summary>
    /// Seconds to delay before retrying a stuck post.
    /// </summary>
    public int StuckPostRetryDelaySeconds { get; set; }

    /// <summary>
    /// Media download URL expiration in minutes (for image publishing).
    /// </summary>
    public int MediaDownloadUrlExpirationMinutes { get; set; }

    /// <summary>
    /// Video download URL expiration in minutes (for Facebook story video publishing).
    /// </summary>
    public int VideoDownloadUrlExpirationMinutes { get; set; }

    /// <summary>
    /// Max polling attempts for Instagram image container status checks.
    /// </summary>
    public int ImagePollMaxAttempts { get; set; }

    /// <summary>
    /// Interval in seconds between Instagram image container status polls.
    /// </summary>
    public int ImagePollIntervalSeconds { get; set; }

    /// <summary>
    /// OAuth state parameter expiration in minutes.
    /// </summary>
    public int OAuthStateExpirationMinutes { get; set; }
}
