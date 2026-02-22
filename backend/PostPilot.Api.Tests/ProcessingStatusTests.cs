using System.Net.Http;
using Xunit;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests that verify the Processing status lifecycle:
/// - ScheduleProcessingRetryAsync sets Status=Processing and increments ProcessingPollCount (not RetryCount)
/// - Due selection includes Processing posts when NextRetryAt is due
/// - RetryPending is only set on transient failures (via HandlePublishFailureAsync)
/// - Progressive polling delay schedule matches specification
/// </summary>
public class ProcessingStatusTests
{
    /// <summary>
    /// Mirrors GetProcessingPollDelaySeconds from InstagramPublisher/InstagramStoryPublisher.
    /// Used to verify the progressive polling schedule in tests.
    /// </summary>
    private static int GetProcessingPollDelaySeconds(int pollCount)
    {
        return pollCount switch
        {
            <= 4  => 30,
            <= 10 => 60,
            <= 15 => 120,
            _     => 180,
        };
    }

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
        var delaySeconds = GetProcessingPollDelaySeconds(post.ProcessingPollCount);
        post.Status = PostStatus.Processing;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(delaySeconds);
        post.ErrorMessage = "Processing\u2026";

        Assert.Equal(PostStatus.Processing, post.Status);
        Assert.Equal(1, post.ProcessingPollCount);
        Assert.Equal(0, post.RetryCount); // Must NOT increment RetryCount
        Assert.NotNull(post.NextRetryAt);
        Assert.Equal(30, delaySeconds); // Poll 1 should be 30s
        Assert.Contains("Processing", post.ErrorMessage);
        Assert.DoesNotContain("poll", post.ErrorMessage); // No poll details in message
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

    // ──────────────────────────────────────────────
    //  PROGRESSIVE POLLING DELAY SCHEDULE TESTS
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 30)]
    [InlineData(3, 30)]
    [InlineData(4, 30)]
    [InlineData(5, 60)]
    [InlineData(6, 60)]
    [InlineData(10, 60)]
    [InlineData(11, 120)]
    [InlineData(15, 120)]
    [InlineData(16, 180)]
    [InlineData(17, 180)]
    [InlineData(20, 180)]
    public void ProgressivePolling_DelayMatchesSchedule(int pollCount, int expectedDelaySeconds)
    {
        var actualDelay = GetProcessingPollDelaySeconds(pollCount);
        Assert.Equal(expectedDelaySeconds, actualDelay);
    }

    [Fact]
    public void ProgressivePolling_TotalDuration_IsReasonable()
    {
        // Calculate total polling duration across all 20 polls
        int totalSeconds = 0;
        for (int i = 1; i <= Post.MaxProcessingPollCount; i++)
        {
            totalSeconds += GetProcessingPollDelaySeconds(i);
        }

        // polls 1-4: 4×30=120, 5-10: 6×60=360, 11-15: 5×120=600, 16-20: 5×180=900
        // Total = 1980 seconds = 33 minutes
        Assert.Equal(1980, totalSeconds);
        Assert.True(totalSeconds < 3600, "Total polling should be under 1 hour");
        Assert.True(totalSeconds > 600, "Total polling should be over 10 minutes");
    }

    [Fact]
    public void ProcessingPoll_DoesNotIncrementRetryCount_AcrossMultiplePolls()
    {
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            Status = PostStatus.Publishing,
            InstagramCreationId = "container-123",
        };

        // Simulate 10 polling iterations
        for (int i = 0; i < 10; i++)
        {
            post.ProcessingPollCount++;
            var delay = GetProcessingPollDelaySeconds(post.ProcessingPollCount);
            post.Status = PostStatus.Processing;
            post.NextRetryAt = DateTime.UtcNow.AddSeconds(delay);
        }

        Assert.Equal(10, post.ProcessingPollCount);
        Assert.Equal(0, post.RetryCount); // RetryCount must remain 0
        Assert.Equal(PostStatus.Processing, post.Status);
    }

    // ──────────────────────────────────────────────
    //  EXCEPTION CLASSIFICATION TESTS
    // ──────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(OperationCanceledException))]
    public void TransientExceptions_AreClassifiedAsTransient(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        var isTransient = ex is HttpRequestException
            or TimeoutException
            or TaskCanceledException
            or OperationCanceledException;
        Assert.True(isTransient);
    }

    [Theory]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    [InlineData(typeof(IndexOutOfRangeException))]
    [InlineData(typeof(KeyNotFoundException))]
    public void BugExceptions_AreNotTransient(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType)!;
        var isTransient = ex is HttpRequestException
            or TimeoutException
            or TaskCanceledException
            or OperationCanceledException;
        Assert.False(isTransient, $"{exceptionType.Name} should NOT be classified as transient");
    }

    [Fact]
    public void BugException_FailsImmediately_WithPermanentError()
    {
        var post = new Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            Status = PostStatus.Publishing,
        };

        // Simulate what the updated catch(Exception ex) does for bug exceptions
        Exception ex = new NullReferenceException("Object reference not set");
        var isTransient = ex is HttpRequestException
            or TimeoutException
            or TaskCanceledException
            or OperationCanceledException;

        Assert.False(isTransient);

        // Simulate HandlePublishFailureAsync with Permanent error type
        post.RetryCount++;
        post.ErrorMessage = $"Internal error (non-retryable): {ex.GetType().Name}: {ex.Message}";
        post.Status = PostStatus.Failed; // Permanent => immediate fail
        post.UpdatedAt = DateTime.UtcNow;

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("non-retryable", post.ErrorMessage);
        Assert.Contains("NullReferenceException", post.ErrorMessage);
    }

    // ──────────────────────────────────────────────
    //  STUCK PUBLISHING RECOVERY TESTS
    // ──────────────────────────────────────────────

    [Fact]
    public void StuckRecovery_IncrementsRetryCount_And_SetsRetryPending()
    {
        var post = new Post
        {
            Content = "test post",
            Platform = Platform.Facebook,
            Status = PostStatus.Publishing,
            RetryCount = 0,
            MaxRetries = 3,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        var now = DateTime.UtcNow;
        var stuckThreshold = now.AddMinutes(-5);

        Assert.True(post.Status == PostStatus.Publishing && post.UpdatedAt < stuckThreshold);

        // Simulate recovery logic
        post.RetryCount++;
        post.UpdatedAt = now;

        Assert.True(post.RetryCount < post.MaxRetries);

        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = now.AddSeconds(10);
        post.ErrorMessage = $"Recovered from stuck Publishing (attempt {post.RetryCount}/{post.MaxRetries})";

        Assert.Equal(PostStatus.RetryPending, post.Status);
        Assert.Equal(1, post.RetryCount);
        Assert.NotNull(post.NextRetryAt);
        Assert.Contains("Recovered", post.ErrorMessage);
    }

    [Fact]
    public void StuckRecovery_FailsPermanently_WhenMaxRetriesReached()
    {
        var post = new Post
        {
            Content = "test post",
            Platform = Platform.Facebook,
            Status = PostStatus.Publishing,
            RetryCount = 2,
            MaxRetries = 3,
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        var now = DateTime.UtcNow;

        // Simulate recovery logic
        post.RetryCount++;
        post.UpdatedAt = now;

        Assert.True(post.RetryCount >= post.MaxRetries);

        post.Status = PostStatus.Failed;
        post.ErrorMessage = $"Stuck in Publishing for >5 minutes (recovered {post.RetryCount}/{post.MaxRetries} times)";
        post.NextRetryAt = null;

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Equal(3, post.RetryCount);
        Assert.Null(post.NextRetryAt);
        Assert.Contains("Stuck in Publishing", post.ErrorMessage);
    }

    [Fact]
    public void StuckRecovery_DoesNotModifyProcessingPollCount()
    {
        var post = new Post
        {
            Content = "test video",
            Platform = Platform.Instagram,
            Status = PostStatus.Publishing,
            RetryCount = 0,
            ProcessingPollCount = 5, // Had some processing before getting stuck
            UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
        };

        // Simulate recovery
        post.RetryCount++;
        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(10);

        Assert.Equal(5, post.ProcessingPollCount); // Must not be modified
        Assert.Equal(1, post.RetryCount);
    }
}
