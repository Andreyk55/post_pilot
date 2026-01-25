using System.Text.Json;
using System.Text.Json.Serialization;
using PostPilot.Api.DTOs;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Service for fetching Facebook post insights and engagement metrics via Meta Graph API.
/// </summary>
public class FacebookInsightsService : IFacebookInsightsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FacebookInsightsService> _logger;

    private const string GraphApiBaseUrl = "https://graph.facebook.com/v21.0";

    public FacebookInsightsService(
        HttpClient httpClient,
        ILogger<FacebookInsightsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<PostEngagementDto?> GetPostEngagementAsync(
        string externalPostId,
        string pageAccessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(externalPostId) || string.IsNullOrEmpty(pageAccessToken))
        {
            _logger.LogWarning("Cannot fetch engagement: missing post ID or access token");
            return null;
        }

        try
        {
            // Fetch post with engagement summary
            // Use reactions (modern) and likes (legacy) for broader compatibility
            // Also include shares and comments with summaries
            var fields = "reactions.summary(total_count),likes.summary(true),comments.summary(true),shares";
            var url = $"{GraphApiBaseUrl}/{externalPostId}?fields={fields}&access_token={pageAccessToken}";

            _logger.LogInformation("Fetching engagement for post {PostId} from URL: {Url}",
                externalPostId, url.Replace(pageAccessToken, "[REDACTED]"));

            var response = await _httpClient.GetAsync(url, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogInformation("Meta API response for engagement {PostId}: {StatusCode} - {Body}",
                externalPostId, response.StatusCode, responseBody);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Failed to fetch engagement for post {PostId}: {StatusCode} - {Body}",
                    externalPostId, response.StatusCode, responseBody);
                return null;
            }

            var result = JsonSerializer.Deserialize<FacebookPostEngagementResponse>(responseBody);
            if (result == null)
            {
                _logger.LogWarning("Failed to parse engagement response for post {PostId}: {Body}",
                    externalPostId, responseBody);
                return null;
            }

            // Use reactions count if available, otherwise fall back to likes
            var likesCount = result.Reactions?.Summary?.TotalCount
                ?? result.Likes?.Summary?.TotalCount;

            var engagement = new PostEngagementDto(
                LikesCount: likesCount,
                CommentsCount: result.Comments?.Summary?.TotalCount,
                SharesCount: result.Shares?.Count
            );

            _logger.LogInformation(
                "Fetched engagement for post {PostId}: Likes/Reactions={Likes}, Comments={Comments}, Shares={Shares}",
                externalPostId, engagement.LikesCount, engagement.CommentsCount, engagement.SharesCount);

            return engagement;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network error fetching engagement for post {PostId}", externalPostId);
            return null;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogError(ex, "Timeout fetching engagement for post {PostId}", externalPostId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error fetching engagement for post {PostId}", externalPostId);
            return null;
        }
    }
}

// Response models for Meta Graph API engagement data
internal class FacebookPostEngagementResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("reactions")]
    public FacebookEngagementData? Reactions { get; set; }

    [JsonPropertyName("likes")]
    public FacebookEngagementData? Likes { get; set; }

    [JsonPropertyName("comments")]
    public FacebookEngagementData? Comments { get; set; }

    [JsonPropertyName("shares")]
    public FacebookSharesData? Shares { get; set; }
}

internal class FacebookEngagementData
{
    [JsonPropertyName("summary")]
    public FacebookEngagementSummary? Summary { get; set; }
}

internal class FacebookEngagementSummary
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }
}

internal class FacebookSharesData
{
    [JsonPropertyName("count")]
    public int Count { get; set; }
}
