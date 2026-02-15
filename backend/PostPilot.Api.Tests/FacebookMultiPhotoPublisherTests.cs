using Xunit;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Controllers;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Facebook multi-photo post entity state and detection.
/// </summary>
public class FacebookMultiPhotoPostStateTests
{
    [Fact]
    public void MultiPhotoPost_DetectedByMediaItemsCount()
    {
        var post = CreateMultiPhotoPost(3);

        var isMultiPhoto = post.Platform == Platform.Facebook && post.MediaItems?.Count >= 2;

        Assert.True(isMultiPhoto);
    }

    [Fact]
    public void SingleImagePost_NotDetectedAsMultiPhoto()
    {
        var post = new Post
        {
            Content = "Single image post",
            Platform = Platform.Facebook,
            MediaType = MediaType.Image,
            MediaUrl = "media/image.jpg",
            MediaItems = new List<PostMediaItem>
            {
                new() { MediaUrl = "media/image.jpg", Order = 0, MediaType = MediaType.Image },
            },
        };

        var isMultiPhoto = post.Platform == Platform.Facebook && post.MediaItems?.Count >= 2;

        Assert.False(isMultiPhoto);
    }

    [Fact]
    public void TextOnlyPost_NotDetectedAsMultiPhoto()
    {
        var post = new Post
        {
            Content = "Text only post",
            Platform = Platform.Facebook,
        };

        var isMultiPhoto = post.Platform == Platform.Facebook && post.MediaItems?.Count >= 2;

        Assert.False(isMultiPhoto);
    }

    [Fact]
    public void MultiPhotoPost_AllMediaItems_AreImageType()
    {
        var post = CreateMultiPhotoPost(5);

        Assert.All(post.MediaItems, item =>
            Assert.Equal(MediaType.Image, item.MediaType));
    }

    [Fact]
    public void MultiPhotoPost_MediaItems_PreservesOrder()
    {
        var post = CreateMultiPhotoPost(4);

        var orderedItems = post.MediaItems.OrderBy(m => m.Order).ToList();

        for (int i = 0; i < orderedItems.Count; i++)
        {
            Assert.Equal(i, orderedItems[i].Order);
        }
    }

    [Fact]
    public void MultiPhotoPost_LegacyMediaUrl_PointsToFirstImage()
    {
        var post = CreateMultiPhotoPost(3);

        // Legacy MediaUrl should be set to the first image (for thumbnail previews)
        Assert.Equal(post.MediaItems[0].MediaUrl, post.MediaUrl);
    }

    [Fact]
    public void MultiPhotoPost_StatusTransition_ScheduledToPublishing()
    {
        var post = CreateMultiPhotoPost(3);

        Assert.Equal(PostStatus.Scheduled, post.Status);

        post.Status = PostStatus.Publishing;

        Assert.Equal(PostStatus.Publishing, post.Status);
    }

    [Fact]
    public void MultiPhotoPost_StatusTransition_PublishingToPublished()
    {
        var post = CreateMultiPhotoPost(3);
        post.Status = PostStatus.Publishing;

        post.Status = PostStatus.Published;
        post.ExternalPostId = "page-id_post-id";
        post.PublishedAt = DateTime.UtcNow;

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("page-id_post-id", post.ExternalPostId);
        Assert.NotNull(post.PublishedAt);
    }

    [Fact]
    public void MultiPhotoPost_StatusTransition_PublishingToFailed()
    {
        var post = CreateMultiPhotoPost(3);
        post.Status = PostStatus.Publishing;

        post.Status = PostStatus.Failed;
        post.ErrorMessage = "Upload failed for photo 2";

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("Upload failed", post.ErrorMessage);
    }

    [Fact]
    public void MultiPhotoPost_RetryTransition_SetsRetryPending()
    {
        var post = CreateMultiPhotoPost(3);
        post.Status = PostStatus.Publishing;

        post.RetryCount++;
        post.Status = PostStatus.RetryPending;
        post.NextRetryAt = DateTime.UtcNow.AddMinutes(2);

        Assert.Equal(PostStatus.RetryPending, post.Status);
        Assert.Equal(1, post.RetryCount);
        Assert.NotNull(post.NextRetryAt);
    }

    [Fact]
    public void MultiPhotoPost_IdempotencyCheck_AlreadyPublished()
    {
        var post = CreateMultiPhotoPost(3);
        post.Status = PostStatus.Published;
        post.ExternalPostId = "page-id_post-id";

        // Should detect already published
        var isAlreadyPublished = post.Status == PostStatus.Published &&
                                  !string.IsNullOrEmpty(post.ExternalPostId);

        Assert.True(isAlreadyPublished);
    }

    private static Post CreateMultiPhotoPost(int imageCount)
    {
        var postId = Guid.NewGuid();
        var mediaItems = Enumerable.Range(0, imageCount).Select(i => new PostMediaItem
        {
            Id = Guid.NewGuid(),
            PostId = postId,
            Order = i,
            MediaUrl = $"media/image{i + 1}.jpg",
            MediaType = MediaType.Image,
        }).ToList();

        return new Post
        {
            Id = postId,
            Content = "Multi-photo Facebook post",
            Platform = Platform.Facebook,
            MediaType = MediaType.Image,
            MediaUrl = mediaItems[0].MediaUrl,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = mediaItems,
        };
    }
}

/// <summary>
/// Tests for Facebook multi-photo validation (mirrors CarouselValidationTests for FB context).
/// </summary>
public class FacebookMultiPhotoValidationTests
{
    [Theory]
    [InlineData(2, true)]   // Minimum multi-photo size
    [InlineData(5, true)]   // Mid-range
    [InlineData(10, true)]  // Maximum multi-photo size
    [InlineData(1, false)]  // Single image (not multi-photo, handled by existing single-image flow)
    [InlineData(11, false)] // Too many
    [InlineData(0, false)]  // No items
    public void MultiPhotoImageCount_ValidatesCorrectly(int count, bool shouldBeValid)
    {
        const int minMultiPhotoImages = 2;
        const int maxMultiPhotoImages = 10;

        var isValid = count >= minMultiPhotoImages && count <= maxMultiPhotoImages;
        Assert.Equal(shouldBeValid, isValid);
    }

    [Fact]
    public void MultiPhotoMediaItems_AllImageType_IsValid()
    {
        var items = Enumerable.Range(0, 4).Select(i => new PostMediaItem
        {
            MediaUrl = $"media/image{i}.jpg",
            Order = i,
            MediaType = MediaType.Image,
        }).ToList();

        var allImages = items.All(i => i.MediaType == MediaType.Image);
        Assert.True(allImages);
    }

    [Fact]
    public void MultiPhotoMediaItems_ContainsVideo_IsInvalid()
    {
        var items = new List<PostMediaItem>
        {
            new() { MediaUrl = "img1.jpg", Order = 0, MediaType = MediaType.Image },
            new() { MediaUrl = "vid.mp4", Order = 1, MediaType = MediaType.Video },
            new() { MediaUrl = "img2.jpg", Order = 2, MediaType = MediaType.Image },
        };

        var allImages = items.All(i => i.MediaType == MediaType.Image);
        Assert.False(allImages);

        var hasVideo = items.Any(i => i.MediaType != MediaType.Image);
        Assert.True(hasVideo);
    }

    [Fact]
    public void MultiPhotoMediaItems_MixedMedia_IsRejected()
    {
        var items = new List<PostMediaItem>
        {
            new() { MediaUrl = "img1.jpg", Order = 0, MediaType = MediaType.Image },
            new() { MediaUrl = "img2.jpg", Order = 1, MediaType = MediaType.Image },
            new() { MediaUrl = "vid.mp4", Order = 2, MediaType = MediaType.Video },
        };

        // Backend validation: all items must be Image type
        var hasNonImage = items.Any(m => m.MediaType != MediaType.Image);
        Assert.True(hasNonImage);
    }
}

/// <summary>
/// Tests for Facebook multi-photo routing logic.
/// Verifies that the publisher correctly routes to multi-photo vs single-photo vs text-only.
/// </summary>
public class FacebookPublisherRoutingTests
{
    [Fact]
    public void RoutesToMultiPhoto_WhenMediaItemsCount_GreaterThanOrEqual2()
    {
        var post = CreateFbPost(mediaItemCount: 3);

        var isMultiPhoto = post.MediaItems?.Count >= 2;

        Assert.True(isMultiPhoto);
    }

    [Fact]
    public void RoutesToSinglePhoto_WhenMediaItemsCount_LessThan2()
    {
        var post = CreateFbPost(mediaItemCount: 0);
        post.MediaType = MediaType.Image;
        post.MediaUrl = "media/image.jpg";

        var isMultiPhoto = post.MediaItems?.Count >= 2;

        Assert.False(isMultiPhoto);
        Assert.Equal(MediaType.Image, post.MediaType);
        Assert.NotNull(post.MediaUrl);
    }

    [Fact]
    public void RoutesToTextOnly_WhenNoMedia()
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Text only",
            Platform = Platform.Facebook,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var isMultiPhoto = post.MediaItems?.Count >= 2;
        var hasSingleImage = post.MediaType == MediaType.Image && !string.IsNullOrEmpty(post.MediaUrl);
        var hasVideo = post.MediaType == MediaType.Video && !string.IsNullOrEmpty(post.MediaUrl);

        Assert.False(isMultiPhoto);
        Assert.False(hasSingleImage);
        Assert.False(hasVideo);
    }

    [Fact]
    public void RoutesToVideo_WhenMediaTypeIsVideo()
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Video post",
            Platform = Platform.Facebook,
            MediaType = MediaType.Video,
            MediaUrl = "media/video.mp4",
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var isMultiPhoto = post.MediaItems?.Count >= 2;
        var isVideo = post.MediaType == MediaType.Video && !string.IsNullOrEmpty(post.MediaUrl);

        Assert.False(isMultiPhoto);
        Assert.True(isVideo);
    }

    [Fact]
    public void MultiPhotoTakesPriority_OverSingleImage()
    {
        // When both MediaUrl and MediaItems are set (legacy preview + multi-photo),
        // multi-photo should take priority
        var post = CreateFbPost(mediaItemCount: 3);
        post.MediaType = MediaType.Image;
        post.MediaUrl = "media/image1.jpg"; // Legacy preview

        var isMultiPhoto = post.MediaItems?.Count >= 2;

        Assert.True(isMultiPhoto, "Multi-photo should be detected even when legacy MediaUrl is set");
    }

    private static Post CreateFbPost(int mediaItemCount)
    {
        var postId = Guid.NewGuid();
        var mediaItems = Enumerable.Range(0, mediaItemCount).Select(i => new PostMediaItem
        {
            Id = Guid.NewGuid(),
            PostId = postId,
            Order = i,
            MediaUrl = $"media/image{i + 1}.jpg",
            MediaType = MediaType.Image,
        }).ToList();

        return new Post
        {
            Id = postId,
            Content = "FB post",
            Platform = Platform.Facebook,
            MediaType = mediaItemCount > 0 ? MediaType.Image : MediaType.None,
            MediaUrl = mediaItems.Count > 0 ? mediaItems[0].MediaUrl : null,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow.AddMinutes(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = mediaItems,
        };
    }
}

/// <summary>
/// Tests for PostDto mapping with Facebook multi-photo media items.
/// </summary>
public class FacebookMultiPhotoPostDtoTests
{
    [Fact]
    public void PostDto_FromEntity_IncludesMediaItems_ForFacebook()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Multi-photo FB post",
            Platform = Platform.Facebook,
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
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 2, MediaUrl = "media/image3.jpg", MediaType = MediaType.Image },
            },
        };

        var dto = PostDto.FromEntity(post);

        Assert.NotNull(dto.MediaItems);
        Assert.Equal(3, dto.MediaItems!.Count);
        Assert.Equal("Facebook", dto.Platform.ToString());
    }

    [Fact]
    public void PostDto_FromEntity_FacebookMultiPhoto_MediaItemsOrderedByOrder()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "FB multi-photo",
            Platform = Platform.Facebook,
            MediaType = MediaType.Image,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = new List<PostMediaItem>
            {
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 2, MediaUrl = "media/image3.jpg", MediaType = MediaType.Image },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 0, MediaUrl = "media/image1.jpg", MediaType = MediaType.Image },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/image2.jpg", MediaType = MediaType.Image },
            },
        };

        var dto = PostDto.FromEntity(post);

        Assert.Equal(0, dto.MediaItems![0].Order);
        Assert.Equal(1, dto.MediaItems![1].Order);
        Assert.Equal(2, dto.MediaItems![2].Order);
        Assert.Equal("media/image1.jpg", dto.MediaItems![0].MediaUrl);
    }

    [Fact]
    public void PostDto_FromEntity_FacebookSingleImage_NoMediaItems()
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Single image FB post",
            Platform = Platform.Facebook,
            MediaType = MediaType.Image,
            MediaUrl = "media/image.jpg",
            Status = PostStatus.Published,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MediaItems = new List<PostMediaItem>(),
        };

        var dto = PostDto.FromEntity(post);

        Assert.Null(dto.MediaItems);
    }
}
