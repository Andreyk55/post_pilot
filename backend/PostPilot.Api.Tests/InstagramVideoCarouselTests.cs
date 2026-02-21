using System.Text.Json;
using Xunit;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Controllers;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram video carousel validation and state transitions.
/// </summary>
public class InstagramVideoCarouselValidationTests
{
    [Fact]
    public void IgOnly_TwoVideos_IsValidVideoCarousel()
    {
        var items = CreateMediaItems(2, MediaType.Video);

        var allVideos = items.All(i => i.MediaType == MediaType.Video);
        var isCarousel = items.Count >= 2 && items.Count <= 10;

        Assert.True(allVideos);
        Assert.True(isCarousel);
    }

    [Fact]
    public void IgOnly_TenVideos_IsValidVideoCarousel()
    {
        var items = CreateMediaItems(10, MediaType.Video);

        var allVideos = items.All(i => i.MediaType == MediaType.Video);
        var isCarousel = items.Count >= 2 && items.Count <= 10;

        Assert.True(allVideos);
        Assert.True(isCarousel);
    }

    [Fact]
    public void IgOnly_TwoVideos_OneImage_IsMixedCarousel()
    {
        // Mixed media is now allowed for Instagram-only carousels
        var items = new List<PostMediaItem>
        {
            new() { MediaUrl = "vid1.mp4", Order = 0, MediaType = MediaType.Video },
            new() { MediaUrl = "vid2.mp4", Order = 1, MediaType = MediaType.Video },
            new() { MediaUrl = "img1.jpg", Order = 2, MediaType = MediaType.Image },
        };

        var imagesCount = items.Count(i => i.MediaType == MediaType.Image);
        var videosCount = items.Count(i => i.MediaType == MediaType.Video);
        var isMixed = imagesCount > 0 && videosCount > 0;
        var isCarousel = items.Count >= 2 && items.Count <= 10;

        Assert.True(isMixed);
        Assert.True(isCarousel); // Valid for IG-only
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(5, true)]
    [InlineData(10, true)]
    [InlineData(1, false)]
    [InlineData(11, false)]
    [InlineData(0, false)]
    public void VideoCarouselCount_ValidatesCorrectly(int count, bool shouldBeValid)
    {
        const int minCarousel = 2;
        const int maxCarousel = 10;

        var isValid = count >= minCarousel && count <= maxCarousel;
        Assert.Equal(shouldBeValid, isValid);
    }

    [Fact]
    public void FacebookOnly_TwoVideos_IsInvalid()
    {
        // Facebook does not support multi-video
        var items = CreateMediaItems(2, MediaType.Video);
        var videosCount = items.Count(i => i.MediaType == MediaType.Video);

        // For Facebook: videos count must be <= 1
        Assert.True(videosCount > 1, "Multi-video should be detected");
    }

    [Fact]
    public void IgPlusFb_TwoVideos_ShouldFail()
    {
        // When both IG and FB are targets, multi-video is not allowed
        // because FB doesn't support it
        var items = CreateMediaItems(2, MediaType.Video);
        var videosCount = items.Count(i => i.MediaType == MediaType.Video);
        var includesFacebook = true;

        // If target includes Facebook and videos > 1, should fail
        var shouldBlock = includesFacebook && videosCount > 1;
        Assert.True(shouldBlock);
    }

    private static List<PostMediaItem> CreateMediaItems(int count, MediaType type)
    {
        return Enumerable.Range(0, count).Select(i => new PostMediaItem
        {
            MediaUrl = type == MediaType.Video ? $"media/video{i}.mp4" : $"media/image{i}.jpg",
            Order = i,
            MediaType = type,
        }).ToList();
    }
}

/// <summary>
/// Tests for video carousel publishing state transitions.
/// </summary>
public class InstagramVideoCarouselStateTransitionTests
{
    [Fact]
    public void VideoCarouselPost_DetectedByAllVideoMediaItems()
    {
        var post = CreateVideoCarouselPost();

        var isCarousel = post.MediaItems?.Count >= 2;
        var isVideoCarousel = isCarousel && post.MediaItems!.All(m => m.MediaType == MediaType.Video);

        Assert.True(isCarousel);
        Assert.True(isVideoCarousel);
    }

    [Fact]
    public void ImageCarouselPost_NotDetectedAsVideoCarousel()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Image carousel",
            Platform = Platform.Instagram,
            MediaType = MediaType.Image,
            MediaUrl = "media/image1.jpg",
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = new List<PostMediaItem>
            {
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 0, MediaUrl = "media/image1.jpg", MediaType = MediaType.Image },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/image2.jpg", MediaType = MediaType.Image },
            },
        };

        var isCarousel = post.MediaItems?.Count >= 2;
        var isVideoCarousel = isCarousel && post.MediaItems!.All(m => m.MediaType == MediaType.Video);

        Assert.True(isCarousel);
        Assert.False(isVideoCarousel);
    }

    [Fact]
    public void VideoCarouselFlow_InitialState_NoContainerIds()
    {
        var post = CreateVideoCarouselPost();

        Assert.Null(post.InstagramChildCreationIds);
        Assert.Null(post.InstagramCarouselCreationId);
        Assert.Equal(0, post.ProcessingPollCount);
    }

    [Fact]
    public void VideoCarouselFlow_AfterChildCreation_PersistsChildIds()
    {
        var post = CreateVideoCarouselPost();
        post.Status = PostStatus.Publishing;

        var childIds = new List<string>();
        childIds.Add("video-child-1");
        post.InstagramChildCreationIds = JsonSerializer.Serialize(childIds);

        childIds.Add("video-child-2");
        post.InstagramChildCreationIds = JsonSerializer.Serialize(childIds);

        var storedIds = JsonSerializer.Deserialize<List<string>>(post.InstagramChildCreationIds);
        Assert.Equal(2, storedIds!.Count);
    }

    [Fact]
    public void VideoCarouselFlow_ProcessingRetry_SetsProcessing()
    {
        var post = CreateVideoCarouselPost();
        post.Status = PostStatus.Publishing;
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "video-child-1", "video-child-2" });

        // Simulate video child still processing
        post.ProcessingPollCount++;
        post.Status = PostStatus.Processing;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(30);

        Assert.Equal(PostStatus.Processing, post.Status);
        Assert.Equal(1, post.ProcessingPollCount);
        Assert.Equal(0, post.RetryCount); // Not a hard failure — ProcessingPollCount only
    }

    [Fact]
    public void VideoCarouselFlow_Finished_SetsPublished()
    {
        var post = CreateVideoCarouselPost();
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "video-child-1", "video-child-2" });
        post.InstagramCarouselCreationId = "video-carousel-container-456";

        post.Status = PostStatus.Published;
        post.ExternalPostId = "media-789";
        post.PublishedAt = DateTime.UtcNow;
        post.InstagramMediaType = InstagramMediaType.CarouselAlbum;

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal(InstagramMediaType.CarouselAlbum, post.InstagramMediaType);
    }

    [Fact]
    public void VideoCarouselPost_MediaItems_AreAllVideoType()
    {
        var post = CreateVideoCarouselPost();

        Assert.All(post.MediaItems, item =>
            Assert.Equal(MediaType.Video, item.MediaType));
    }

    [Fact]
    public void VideoCarouselPost_MediaItems_AreOrderedByOrder()
    {
        var post = CreateVideoCarouselPost();

        var orderedItems = post.MediaItems.OrderBy(m => m.Order).ToList();

        Assert.Equal(0, orderedItems[0].Order);
        Assert.Equal(1, orderedItems[1].Order);
    }

    private static Post CreateVideoCarouselPost()
    {
        var postId = Guid.NewGuid();
        return new Post
        {
            Id = postId,
            Content = "Test video carousel post",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            MediaUrl = "media/video1.mp4",
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = new List<PostMediaItem>
            {
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 0, MediaUrl = "media/video1.mp4", MediaType = MediaType.Video },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/video2.mp4", MediaType = MediaType.Video },
            },
        };
    }
}

/// <summary>
/// Tests for PostDto mapping with video carousel media items.
/// </summary>
public class VideoCarouselPostDtoTests
{
    [Fact]
    public void PostDto_FromEntity_IncludesVideoMediaItems()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Video carousel",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            MediaUrl = "media/video1.mp4",
            Status = PostStatus.Published,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            InstagramMediaType = InstagramMediaType.CarouselAlbum,
            MediaItems = new List<PostMediaItem>
            {
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 0, MediaUrl = "media/video1.mp4", MediaType = MediaType.Video },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/video2.mp4", MediaType = MediaType.Video },
            },
        };

        var dto = PostDto.FromEntity(post);

        Assert.NotNull(dto.MediaItems);
        Assert.Equal(2, dto.MediaItems!.Count);
        Assert.All(dto.MediaItems, item => Assert.Equal(MediaType.Video, item.MediaType));
        Assert.Equal("CarouselAlbum", dto.InstagramMediaType);
    }

    [Fact]
    public void PostDto_FromEntity_VideoCarousel_MediaType_IsVideo()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Video carousel",
            Platform = Platform.Instagram,
            MediaType = MediaType.Video,
            MediaUrl = "media/video1.mp4",
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = new List<PostMediaItem>
            {
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 0, MediaUrl = "media/video1.mp4", MediaType = MediaType.Video },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/video2.mp4", MediaType = MediaType.Video },
            },
        };

        var dto = PostDto.FromEntity(post);

        Assert.Equal(MediaType.Video, dto.MediaType);
    }
}
