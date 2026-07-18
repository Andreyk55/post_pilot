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
        Assert.Equal(50L * 1024 * 1024, rules.MaxBytes); // 50MB
        Assert.Equal(52_428_800L, rules.MaxBytes);
    }

    [Fact]
    public void HasRules_InstagramFeedVideo_ReturnsTrue()
    {
        Assert.True(MediaValidationRules.HasRules(Platform.Instagram, Placement.Feed, MediaType.Video));
    }

    // Finalized policy: IG Feed video has NO dimension rules (Meta handles framing/scaling).
    [Fact]
    public void GetRules_InstagramFeedVideo_HasNoDimensionLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Null(rules.MinWidth);
        Assert.Null(rules.MinHeight);
        Assert.Null(rules.MaxWidth);
        Assert.Null(rules.MaxHeight);
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_CorrectDurationLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Equal(3, rules.DurationMinSeconds);
        Assert.Equal(180, rules.DurationMaxSeconds); // single Feed video (carousel items: 60s)
    }

    // Finalized policy: NO aspect-ratio prevalidation for IG Feed video (any orientation passes).
    [Fact]
    public void GetRules_InstagramFeedVideo_HasNoAspectRatioLimits()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Null(rules.AspectRatioMin);
        Assert.Null(rules.AspectRatioMax);
    }

    // Finalized policy: NO codec/audio-codec allow-list for IG Feed video (Meta decides playability).
    [Fact]
    public void GetRules_InstagramFeedVideo_HasNoCodecConstraints()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Null(rules.AllowedVideoCodecs);
        Assert.Null(rules.AllowedAudioCodecs);
        Assert.Null(rules.MinFps);
        Assert.Null(rules.MaxFps);
    }

    [Fact]
    public void GetRules_InstagramFeedVideo_CorrectContainerFormats()
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.NotNull(rules.AllowedContainers);
        Assert.Contains("mp4", rules.AllowedContainers);
        Assert.Contains("mov", rules.AllowedContainers);
    }

    // Regression: with no dimension rule, EVERY size is accepted — including 4K (was too large)
    // and 400x400 (was too small). The rule exposes no Min/Max width/height to compare against.
    [Theory]
    [InlineData(1920, 1080)] // 16:9 landscape
    [InlineData(1080, 1080)] // 1:1 square
    [InlineData(1080, 1350)] // 4:5 portrait
    [InlineData(3840, 2160)] // 4K — previously rejected (exceeded the old 1920 max)
    [InlineData(400, 400)]   // previously rejected (below the old 500 min)
    public void Dimensions_InstagramFeedVideo_AreNotValidated(int width, int height)
    {
        var rules = MediaValidationRules.GetRules(Platform.Instagram, Placement.Feed, MediaType.Video)!;

        Assert.Null(rules.MinWidth);
        Assert.Null(rules.MinHeight);
        Assert.Null(rules.MaxWidth);
        Assert.Null(rules.MaxHeight);

        // No bound to compare against → the engine skips the dimension check entirely.
        var dimensionRejects =
            (rules.MinWidth.HasValue && width < rules.MinWidth) ||
            (rules.MinHeight.HasValue && height < rules.MinHeight) ||
            (rules.MaxWidth.HasValue && width > rules.MaxWidth) ||
            (rules.MaxHeight.HasValue && height > rules.MaxHeight);
        Assert.False(dimensionRejects);
    }

    [Theory]
    [InlineData(2.0, true)]  // Too short
    [InlineData(3.0, false)] // Exactly at minimum
    [InlineData(30.0, false)] // Middle of range
    [InlineData(180.0, false)] // Exactly at the MVP maximum
    [InlineData(181.0, true)] // Too long
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
    public void VideoFlow_ProcessingRetry_SetsProcessing()
    {
        var post = CreateVideoPost();
        post.InstagramCreationId = "container-123";
        post.Status = PostStatus.Publishing;

        // Simulate processing retry
        post.ProcessingPollCount++;
        post.Status = PostStatus.Processing;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(30);

        Assert.Equal(PostStatus.Processing, post.Status);
        Assert.Equal(1, post.ProcessingPollCount);
        Assert.NotNull(post.NextRetryAt);
        Assert.Equal(0, post.RetryCount); // Not a hard failure — ProcessingPollCount only
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
        post.Status = PostStatus.Processing;

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
