using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Service for fetching Facebook post insights and engagement metrics.
/// </summary>
public interface IFacebookInsightsService
{
    /// <summary>
    /// Fetches engagement metrics for a Facebook post.
    /// </summary>
    /// <param name="externalPostId">The Facebook post ID (format: pageId_postId)</param>
    /// <param name="pageAccessToken">The page access token</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Engagement metrics or null if unavailable</returns>
    Task<PostEngagementDto?> GetPostEngagementAsync(
        string externalPostId,
        string pageAccessToken,
        CancellationToken cancellationToken = default);
}
