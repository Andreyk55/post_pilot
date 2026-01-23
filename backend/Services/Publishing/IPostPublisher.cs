using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Abstraction for publishing posts to social media platforms.
/// </summary>
public interface IPostPublisher
{
    /// <summary>
    /// The platform this publisher handles
    /// </summary>
    Platform SupportedPlatform { get; }

    /// <summary>
    /// Publish a post to the target platform.
    /// Must be idempotent - safe to call multiple times.
    /// </summary>
    /// <param name="postId">ID of the post to publish</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the publishing operation</returns>
    Task<PublishResult> PublishAsync(Guid postId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a publishing operation
/// </summary>
public record PublishResult(
    bool Success,
    string? ExternalPostId = null,
    PublishErrorType? ErrorType = null,
    string? ErrorMessage = null
);

/// <summary>
/// Classification of publishing errors
/// </summary>
public enum PublishErrorType
{
    /// <summary>Temporary error - should retry (rate limit, network, server error)</summary>
    Transient,

    /// <summary>Permanent error - do not retry (invalid content, permissions, etc.)</summary>
    Permanent,

    /// <summary>Post was already published (idempotency check)</summary>
    AlreadyPublished
}
