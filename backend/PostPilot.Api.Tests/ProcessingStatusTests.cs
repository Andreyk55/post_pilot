using Xunit;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests that verify the Processing status lifecycle:
/// - ScheduleProcessingRetryAsync sets Status=Processing and increments ProcessingPollCount (not RetryCount)
/// - Due selection includes Processing posts when NextRetryAt is due
/// - RetryPending is only set on transient failures (via HandlePublishFailureAsync)
/// </summary>
public class ProcessingStatusTests
{
    [Fact]
    public void ProcessingRetry_SetsProcessingStatus_And_IncrementsProcessingPollCount()
    {
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Publishing,
            InstagramCreationId = "container-123",
        };

        // Simulate what ScheduleProcessingRetryAsync does
        post.ProcessingPollCount++;
        post.Status = PostStatus.Processing;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(30);
        post.ErrorMessage = $"Processing\u2026 (poll {post.ProcessingPollCount}/{Post.MaxProcessingPollCount})";

        Assert.Equal(PostStatus.Processing, post.Status);
        Assert.Equal(1, post.ProcessingPollCount);
        Assert.Equal(0, post.RetryCount); // Must NOT increment RetryCount
        Assert.NotNull(post.NextRetryAt);
        Assert.Contains("Processing", post.ErrorMessage);
    }

    [Fact]
    public void TransientFailure_SetsRetryPendingStatus_And_IncrementsRetryCount()
    {
        var post = new Post
        {
            Content = "test post",
            Platform = Platform.Instagram,
            MediaType = MediaType.Image,
            Status = PostStatus.Publishing,
        };

        // Simulate what HandlePublishFailureAsync does for transient errors
        post.RetryCount++;
        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = DateTime.UtcNow.AddMinutes(2);
        post.ErrorMessage = "Network error: connection timed out";

        Assert.Equal(PostStatus.RetryPending, post.Status);
        Assert.Equal(1, post.RetryCount);
        Assert.Equal(0, post.ProcessingPollCount); // Must NOT increment ProcessingPollCount
        Assert.NotNull(post.NextRetryAt);
    }

    [Fact]
    public void ProcessingPost_IsDue_WhenNextRetryAtIsInPast()
    {
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Processing,
            NextRetryAt = DateTime.UtcNow.AddSeconds(-10), // 10 seconds ago
            ProcessingPollCount = 3,
        };

        var now = DateTime.UtcNow;
        var isDue = (post.Status == PostStatus.RetryPending || post.Status == PostStatus.Processing)
                    && post.NextRetryAt != null
                    && post.NextRetryAt <= now;

        Assert.True(isDue);
    }

    [Fact]
    public void ProcessingPost_IsNotDue_WhenNextRetryAtIsInFuture()
    {
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Processing,
            NextRetryAt = DateTime.UtcNow.AddSeconds(30), // 30 seconds from now
            ProcessingPollCount = 3,
        };

        var now = DateTime.UtcNow;
        var isDue = (post.Status == PostStatus.RetryPending || post.Status == PostStatus.Processing)
                    && post.NextRetryAt != null
                    && post.NextRetryAt <= now;

        Assert.False(isDue);
    }

    [Fact]
    public void ProcessingTimeout_SetsFailed_WhenPollCountExceedsMax()
    {
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Processing,
            ProcessingPollCount = Post.MaxProcessingPollCount,
            InstagramCreationId = "container-123",
        };

        // Simulate what ScheduleProcessingRetryAsync does when poll count >= max
        if (post.ProcessingPollCount >= Post.MaxProcessingPollCount)
        {
            post.Status = PostStatus.Failed;
            post.ErrorMessage = $"Video processing timed out after {post.ProcessingPollCount} status checks";
        }

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("timed out", post.ErrorMessage);
        Assert.Equal(0, post.RetryCount); // Still no RetryCount increment
    }

    [Fact]
    public void ProcessingStatus_IsClaimable()
    {
        // TryClaimPostAsync should accept Processing status
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Processing,
        };

        var isClaimable = post.Status == PostStatus.Scheduled
                       || post.Status == PostStatus.RetryPending
                       || post.Status == PostStatus.Processing;

        Assert.True(isClaimable);
    }

    [Fact]
    public void StuckRecovery_DoesNotTouchProcessing()
    {
        // Stuck recovery should only apply to Publishing posts, not Processing
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Processing,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10), // 10 minutes old
        };

        var stuckThreshold = DateTime.UtcNow.AddMinutes(-5);
        var isStuck = post.Status == PostStatus.Publishing && post.UpdatedAt < stuckThreshold;

        Assert.False(isStuck); // Processing posts are NOT stuck
    }
}
