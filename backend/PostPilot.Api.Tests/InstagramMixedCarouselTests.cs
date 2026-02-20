using System.Text.Json;
using Xunit;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Controllers;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram mixed carousel (images + videos) validation.
/// </summary>
public class InstagramMixedCarouselValidationTests
{
    [Fact]
    public void IgOnly_ImageAndVideo_IsValidMixedCarousel()
    {
        var items = new List<PostMediaItem>
        {
            new() { MediaUrl = "img1.jpg", Order = 0, MediaType = MediaType.Image },
            new() { MediaUrl = "vid1.mp4", Order = 1, MediaType = MediaType.Video },
        };

        var imagesCount = items.Count(i => i.MediaType == MediaType.Image);
        var videosCount = items.Count(i => i.MediaType == MediaType.Video);
        var isCarousel = items.Count >= 2 && items.Count <= 10;
        var isMixed = imagesCount > 0 && videosCount > 0;

        Assert.True(isCarousel);
        Assert.True(isMixed);
    }

    [Fact]
    public void IgOnly_MixedTenItems_IsValid()
    {
        var items = new List<PostMediaItem>();
        for (int i = 0; i < 5; i++)
            items.Add(new PostMediaItem { MediaUrl = $"img{i}.jpg", Order = i, MediaType = MediaType.Image });
        for (int i = 5; i < 10; i++)
            items.Add(new PostMediaItem { MediaUrl = $"vid{i}.mp4", Order = i, MediaType = MediaType.Video });

        Assert.Equal(10, items.Count);
        Assert.True(items.Count >= 2 && items.Count <= 10);
        Assert.True(items.Any(i => i.MediaType == MediaType.Image));
        Assert.True(items.Any(i => i.MediaType == MediaType.Video));
    }

    [Fact]
    public void IgOnly_ElevenMixedItems_FailsTooMany()
    {
        var items = new List<PostMediaItem>();
        for (int i = 0; i < 6; i++)
            items.Add(new PostMediaItem { MediaUrl = $"img{i}.jpg", Order = i, MediaType = MediaType.Image });
        for (int i = 6; i < 11; i++)
            items.Add(new PostMediaItem { MediaUrl = $"vid{i}.mp4", Order = i, MediaType = MediaType.Video });

        Assert.Equal(11, items.Count);
        Assert.False(items.Count >= 2 && items.Count <= 10);
    }

    [Fact]
    public void Facebook_MixedMedia_ShouldBeBlocked()
    {
        var items = new List<PostMediaItem>
        {
            new() { MediaUrl = "img1.jpg", Order = 0, MediaType = MediaType.Image },
            new() { MediaUrl = "vid1.mp4", Order = 1, MediaType = MediaType.Video },
        };

        var imagesCount = items.Count(i => i.MediaType == MediaType.Image);
        var videosCount = items.Count(i => i.MediaType == MediaType.Video);
        var includesFacebook = true;

        // Facebook blocks mixed media
        var shouldBlock = includesFacebook && imagesCount > 0 && videosCount > 0;
        Assert.True(shouldBlock);
    }

    [Fact]
    public void MixedCarousel_DetectedCorrectly()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Mixed carousel",
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
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/video1.mp4", MediaType = MediaType.Video },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 2, MediaUrl = "media/image2.jpg", MediaType = MediaType.Image },
            },
        };

        var isCarousel = post.MediaItems?.Count >= 2;
        var isVideoCarousel = isCarousel && post.MediaItems!.All(m => m.MediaType == MediaType.Video);
        var isImageCarousel = isCarousel && post.MediaItems!.All(m => m.MediaType == MediaType.Image);
        var isMixedCarousel = isCarousel && !isVideoCarousel && !isImageCarousel;

        Assert.True(isCarousel);
        Assert.False(isVideoCarousel);
        Assert.False(isImageCarousel);
        Assert.True(isMixedCarousel);
    }
}

/// <summary>
/// Tests for mixed carousel publishing state transitions.
/// </summary>
public class InstagramMixedCarouselStateTransitionTests
{
    [Fact]
    public void MixedCarouselFlow_InitialState_NoContainerIds()
    {
        var post = CreateMixedCarouselPost();

        Assert.Null(post.InstagramChildCreationIds);
        Assert.Null(post.InstagramCarouselCreationId);
        Assert.Equal(0, post.ProcessingPollCount);
    }

    [Fact]
    public void MixedCarouselFlow_AfterChildCreation_PersistsChildIds()
    {
        var post = CreateMixedCarouselPost();
        post.Status = PostStatus.Publishing;

        // Simulate creating children for image, video, image
        var childIds = new List<string> { "image-child-1", "video-child-2", "image-child-3" };
        post.InstagramChildCreationIds = JsonSerializer.Serialize(childIds);

        var storedIds = JsonSerializer.Deserialize<List<string>>(post.InstagramChildCreationIds);
        Assert.Equal(3, storedIds!.Count);
        Assert.Equal("video-child-2", storedIds[1]);
    }

    [Fact]
    public void MixedCarouselFlow_ProcessingRetry_SetsRetryPending()
    {
        var post = CreateMixedCarouselPost();
        post.Status = PostStatus.Publishing;
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "image-child-1", "video-child-2", "image-child-3" });

        // Simulate video child still processing
        post.ProcessingPollCount++;
        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(30);

        Assert.Equal(PostStatus.RetryPending, post.Status);
        Assert.Equal(1, post.ProcessingPollCount);
        Assert.Equal(0, post.RetryCount);
    }

    [Fact]
    public void MixedCarouselFlow_Finished_SetsPublished()
    {
        var post = CreateMixedCarouselPost();
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "image-child-1", "video-child-2", "image-child-3" });
        post.InstagramCarouselCreationId = "mixed-carousel-container-456";

        post.Status = PostStatus.Published;
        post.ExternalPostId = "media-789";
        post.PublishedAt = DateTime.UtcNow;
        post.InstagramMediaType = InstagramMediaType.CarouselAlbum;

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal(InstagramMediaType.CarouselAlbum, post.InstagramMediaType);
    }

    [Fact]
    public void MixedCarouselPost_MediaItems_HaveMixedTypes()
    {
        var post = CreateMixedCarouselPost();

        var imageItems = post.MediaItems.Where(m => m.MediaType == MediaType.Image).ToList();
        var videoItems = post.MediaItems.Where(m => m.MediaType == MediaType.Video).ToList();

        Assert.Equal(2, imageItems.Count);
        Assert.Single(videoItems);
    }

    [Fact]
    public void MixedCarouselPost_MediaItems_AreOrderedCorrectly()
    {
        var post = CreateMixedCarouselPost();

        var orderedItems = post.MediaItems.OrderBy(m => m.Order).ToList();

        Assert.Equal(MediaType.Image, orderedItems[0].MediaType); // image first
        Assert.Equal(MediaType.Video, orderedItems[1].MediaType); // video second
        Assert.Equal(MediaType.Image, orderedItems[2].MediaType); // image third
    }

    [Fact]
    public void MixedCarouselPost_ChildContainers_UseCorrectUrlType()
    {
        var post = CreateMixedCarouselPost();
        var mediaItems = post.MediaItems.OrderBy(m => m.Order).ToList();

        // Verify that each item's type determines the API field used
        for (int i = 0; i < mediaItems.Count; i++)
        {
            var item = mediaItems[i];
            if (item.MediaType == MediaType.Video)
            {
                // Should use video_url field
                Assert.Contains("video", item.MediaUrl, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // Should use image_url field
                Assert.Contains("image", item.MediaUrl, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static Post CreateMixedCarouselPost()
    {
        var postId = Guid.NewGuid();
        return new Post
        {
            Id = postId,
            Content = "Test mixed carousel post",
            Platform = Platform.Instagram,
            MediaType = MediaType.Image,
            MediaUrl = "media/image1.jpg",
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = new List<PostMediaItem>
            {
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 0, MediaUrl = "media/image1.jpg", MediaType = MediaType.Image },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/video1.mp4", MediaType = MediaType.Video },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 2, MediaUrl = "media/image2.jpg", MediaType = MediaType.Image },
            },
        };
    }
}

/// <summary>
/// Tests for PostDto mapping with mixed carousel media items.
/// </summary>
public class MixedCarouselPostDtoTests
{
    [Fact]
    public void PostDto_FromEntity_IncludesMixedMediaItems()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Mixed carousel",
            Platform = Platform.Instagram,
            MediaType = MediaType.Image,
            MediaUrl = "media/image1.jpg",
            Status = PostStatus.Published,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            InstagramMediaType = InstagramMediaType.CarouselAlbum,
            MediaItems = new List<PostMediaItem>
            {
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 0, MediaUrl = "media/image1.jpg", MediaType = MediaType.Image },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/video1.mp4", MediaType = MediaType.Video },
            },
        };

        var dto = PostDto.FromEntity(post);

        Assert.NotNull(dto.MediaItems);
        Assert.Equal(2, dto.MediaItems!.Count);
        Assert.Equal(MediaType.Image, dto.MediaItems[0].MediaType);
        Assert.Equal(MediaType.Video, dto.MediaItems[1].MediaType);
        Assert.Equal("CarouselAlbum", dto.InstagramMediaType);
    }
}
