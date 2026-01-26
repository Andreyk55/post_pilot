using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// No-op implementation of IFacebookInsightsService that always returns null.
/// Used when engagement fetching is disabled via configuration.
/// </summary>
public class DisabledFacebookInsightsService : IFacebookInsightsService
{
    private readonly ILogger<DisabledFacebookInsightsService> _logger;

    public DisabledFacebookInsightsService(ILogger<DisabledFacebookInsightsService> logger)
    {
        _logger = logger;
    }

    public Task<PostEngagementDto?> GetPostEngagementAsync(
        string externalPostId,
        string pageAccessToken,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Engagement fetch is disabled (Features:EnableEngagementFetch=false). Skipping fetch for post {PostId}", externalPostId);
        return Task.FromResult<PostEngagementDto?>(null);
    }
}
