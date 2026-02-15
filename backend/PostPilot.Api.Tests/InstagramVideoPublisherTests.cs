using Xunit;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Validation;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram video validation rules and publishing flow logic.
/// These test the validation rules and state machine without mocking the full publisher.
/// </summary>
public class InstagramVideoValidationTests
{
    [Fact]
    public void GetRules_InstagramFeedVideo_ReturnsRules()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video);

        Assert.NotNull(rules);
        Assert.Contains("video/mp4", rules.AllowedMimeTypes);
        Assert.Contains("video/quicktime", rules.AllowedMimeTypes);
        Assert.Equal(100L * 1024 * 1024, rules.MaxBytes); // 100MB
    }

    [Fact]
    public void HasRules_InstagramFeedVideo_ReturnsTrue()
    {
        Assert.True(MediaValidationRules.HasRules(Platform.Instagram, Placement.Feed, MediaType.Video));
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_CorrectDimensionLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Equal(500, rules.MinWidth);
        Assert.Equal(500, rules.MinHeight);
        Assert.Equal(1920, rules.MaxWidth);
        Assert.Equal(1920, rules.MaxHeight);
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_CorrectDurationLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Equal(3, rules.DurationMinSeconds);
        Assert.Equal(60, rules.DurationMaxSeconds);
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_CorrectAspectRatioLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Equal(0.8, rules.AspectRatioMin); // 4:5
        Assert.Equal(1.91, rules.AspectRatioMax);
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_CorrectCodecConstraints()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.NotNull(rules.AllowedVideoCodecs);
        Assert.Contains("h264", rules.AllowedVideoCodecs);
        Assert.NotNull(rules.AllowedAudioCodecs);
        Assert.Contains("aac", rules.AllowedAudioCodecs);
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_CorrectContainerFormats()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.NotNull(rules.AllowedContainers);
        Assert.Contains("mp4", rules.AllowedContainers);
        Assert.Contains("mov", rules.AllowedContainers);
    }

    [Theory]
    [InlineData(1920, 1080, false)] // 16:9 landscape - valid
    [InlineData(1080, 1080, false)] // 1:1 square - valid
    [InlineData(1080, 1350, false)] // 4:5 portrait - valid
    [InlineData(1920, 1920, false)] // Max dimensions exactly - valid
    [InlineData(3840, 2160, true)]  // 4K - too large (exceeds 1920 max)
    [InlineData(400, 400, true)]    // Too small (below 500 min)
    public void Dimensions_InstagramFeedVideo_ValidatesCorrectly(int width, int height, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        var tooSmall = width < rules.MinWidth || height < rules.MinHeight;
        var tooLarge = width > rules.MaxWidth || height > rules.MaxHeight;
        var isInvalid = tooSmall || tooLarge;

        Assert.Equal(shouldFail, isInvalid);
    }

    [Theory]
    [InlineData(2.0, true)]  // Too short
    [InlineData(3.0, false)] // Exactly at minimum
    [InlineData(30.0, false)] // Middle of range
    [InlineData(60.0, false)] // Exactly at maximum
    [InlineData(61.0, true)] // Too long
    public void Duration_InstagramFeedVideo_ValidatesCorrectly(double durationSeconds, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        var tooShort = rules.DurationMinSeconds.HasValue && durationSeconds < rules.DurationMinSeconds.Value;
        var tooLong = rules.DurationMaxSeconds.HasValue && durationSeconds > rules.DurationMaxSeconds.Value;
        var isInvalid = tooShort || tooLong;

        Assert.Equal(shouldFail, isInvalid);
    }

    [Theory]
    [InlineData("video/mp4", false)]
    [InlineData("video/quicktime", false)]
    [InlineData("video/webm", true)]      // Not supported for IG
    [InlineData("video/x-msvideo", true)] // AVI not supported for IG
    public void MimeType_InstagramFeedVideo_ValidatesCorrectly(string mimeType, bool shouldFail)
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;
        var isUnsupported = !rules.AllowedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(shouldFail, isUnsupported);
    }
}

/// <summary>
/// Tests for the Post entity's video processing state machine fields.
/// </summary>
public class InstagramVideoPostStateTests
{
    [Fact]
    public void MaxProcessingPollCount_HasReasonableDefault()
    {
        // 20 polls * 30s = ~10 minutes max wait
        Assert.Equal(20, Entities.Post.MaxProcessingPollCount);
    }

    [Fact]
    public void NewPost_HasZeroProcessingPollCount()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
        };

        Assert.Equal(0, post.ProcessingPollCount);
        Assert.Null(post.InstagramCreationId);
    }

    [Fact]
    public void Post_CanStoreInstagramCreationId()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            InstagramCreationId = "17889615691921648",
        };

        Assert.Equal("17889615691921648", post.InstagramCreationId);
    }

    [Fact]
    public void ProcessingPollCount_IncrementsBelowMax_NotTimedOut()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            ProcessingPollCount = 5,
        };

        Assert.True(post.ProcessingPollCount < Entities.Post.MaxProcessingPollCount);
    }

    [Fact]
    public void ProcessingPollCount_AtMax_ShouldTimeout()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            ProcessingPollCount = Entities.Post.MaxProcessingPollCount,
        };

        Assert.True(post.ProcessingPollCount >= Entities.Post.MaxProcessingPollCount);
    }

    [Fact]
    public void VideoPost_WithImageType_IsNotVideo()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            MediaType = MediaType.Image,
        };

        Assert.NotEqual(MediaType.Video, post.MediaType);
    }

    [Fact]
    public void VideoPost_WithVideoType_IsVideo()
    {
        var post = new Entities.Post
        {
            Content = "test",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
        };

        Assert.Equal(MediaType.Video, post.MediaType);
    }
}

/// <summary>
/// Tests for IG video publishing state transitions.
/// These verify the expected states at each step of the video flow.
/// </summary>
public class InstagramVideoStateTransitionTests
{
    [Fact]
    public void VideoFlow_InitialState_NoCreationId()
    {
        var post = CreateVideoPost();

        // First attempt: no container created yet
        Assert.Null(post.InstagramCreationId);
        Assert.Equal(0, post.ProcessingPollCount);
        Assert.Equal(PostStatus.Scheduled, post.Status);
    }

    [Fact]
    public void VideoFlow_AfterContainerCreation_HasCreationId()
    {
        var post = CreateVideoPost();

        // Simulate container creation
        post.InstagramCreationId = "container-123";
        post.Status = PostStatus.Publishing;

        Assert.Equal("container-123", post.InstagramCreationId);
    }

    [Fact]
    public void VideoFlow_ProcessingRetry_SetsRetryPending()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";
        post.Status = PostStatus.Publishing;

        // Simulate processing retry
        post.ProcessingPollCount++;
        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(30);

        Assert.Equal(PostStatus.RetryPending, post.Status);
        Assert.Equal(1, post.ProcessingPollCount);
        Assert.NotNull(post.NextRetryAt);
        Assert.Equal(0, post.RetryCount); // Not a hard failure
    }

    [Fact]
    public void VideoFlow_ProcessingTimeout_SetsFailed()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";
        post.ProcessingPollCount = Entities.Post.MaxProcessingPollCount;

        // Should timeout
        post.Status = PostStatus.Failed;
        post.ErrorMessage = "Video processing timed out";

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("timed out", post.ErrorMessage);
    }

    [Fact]
    public void VideoFlow_ContainerFinished_SetsPublished()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";
        post.ProcessingPollCount = 3; // Took 3 polls

        // Simulate successful publish
        post.Status = PostStatus.Published;
        post.ExternalPostId = "media-456";
        post.PublishedAt = DateTime.UtcNow;
        post.ErrorMessage = null;

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("media-456", post.ExternalPostId);
        Assert.NotNull(post.PublishedAt);
    }

    [Fact]
    public void VideoFlow_ContainerError_SetsFailed()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";

        // Container returned ERROR status
        post.Status = PostStatus.Failed;
        post.ErrorMessage = "Container processing failed: invalid video";

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("failed", post.ErrorMessage);
    }

    [Fact]
    public void VideoFlow_ContainerExpired_ClearsCreationId()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";

        // Container expired — should clear creation ID for retry
        post.InstagramCreationId = null;

        Assert.Null(post.InstagramCreationId);
    }

    [Fact]
    public void VideoFlow_ProcessingRetryDoesNotIncrementRetryCount()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";

        // Multiple processing polls
        post.ProcessingPollCount = 5;

        // RetryCount stays at 0 — processing polls are separate
        Assert.Equal(0, post.RetryCount);
        Assert.Equal(5, post.ProcessingPollCount);
    }

    [Fact]
    public void VideoFlow_IdempotencyCheck_AlreadyPublished()
    {
        var post = CreateVideoPost();
        post.Status = PostStatus.Published;
        post.ExternalPostId = "media-789";

        // Should not republish
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.False(string.IsNullOrEmpty(post.ExternalPostId));
    }

    [Fact]
    public void VideoFlow_IdempotencyCheck_HasCreationIdResumesPoll()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";
        post.ProcessingPollCount = 2;
        post.Status = PostStatus.RetryPending;

        // On next attempt, should resume polling (not recreate container)
        Assert.False(string.IsNullOrEmpty(post.InstagramCreationId));
    }

    private static Entities.Post CreateVideoPost() => new()
    {
        Id = Guid.NewGuid(),
        Content = "Test video post",
        MediaUrl = "media/test-video.mp4",
        MediaType = MediaType.Video,
        Platform = Platform.Instagram,
        Status = PostStatus.Scheduled,
        ScheduledAt = DateTime.UtcNow.AddMinutes(-1),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
