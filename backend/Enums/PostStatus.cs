namespace PostPilot.Api.Enums;

public enum PostStatus
{
    /// <summary>Post is scheduled and waiting for publication time</summary>
    Scheduled,

    /// <summary>Post is currently being published (prevents race conditions)</summary>
    Publishing,

    /// <summary>Post was successfully published to the platform</summary>
    Published,

    /// <summary>Post failed permanently (max retries exceeded or permanent error)</summary>
    Failed,

    /// <summary>Post failed but will be retried (transient error with exponential backoff)</summary>
    RetryPending,

    /// <summary>Post was canceled by the user before publishing</summary>
    Canceled,

    /// <summary>Meta accepted the upload but is still processing (e.g., IG video container IN_PROGRESS). Not a failure.</summary>
    Processing
}
