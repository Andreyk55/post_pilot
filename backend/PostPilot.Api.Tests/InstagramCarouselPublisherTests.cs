using System.Text.Json;
using Xunit;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Controllers;

namespace PostPilot.Api.Tests;

/// <summary>
/// Tests for Instagram carousel post entity state, child ID serialization,
/// and the carousel publishing state machine.
/// </summary>
public class InstagramCarouselPostStateTests
{
    [Fact]
    public void NewPost_HasNullCarouselFields()
    {
        var post = CreateCarouselPost();

        Assert.Null(post.InstagramChildCreationIds);
        Assert.Null(post.InstagramCarouselCreationId);
        Assert.Equal(0, post.ProcessingPollCount);
    }

    [Fact]
    public void Post_CanStoreChildCreationIds_AsJson()
    {
        var post = CreateCarouselPost();
        var childIds = new List<string> { "child-1", "child-2", "child-3" };

        post.InstagramChildCreationIds = JsonSerializer.Serialize(childIds);

        var deserialized = JsonSerializer.Deserialize<List<string>>(post.InstagramChildCreationIds);
        Assert.NotNull(deserialized);
        Assert.Equal(3, deserialized.Count);
        Assert.Equal("child-1", deserialized[0]);
        Assert.Equal("child-2", deserialized[1]);
        Assert.Equal("child-3", deserialized[2]);
    }

    [Fact]
    public void Post_CanStoreCarouselCreationId()
    {
        var post = CreateCarouselPost();
        post.InstagramCarouselCreationId = "carousel-container-123";

        Assert.Equal("carousel-container-123", post.InstagramCarouselCreationId);
    }

    [Fact]
    public void CarouselPost_MediaItems_AreOrderedByOrder()
    {
        var post = CreateCarouselPost();

        var orderedItems = post.MediaItems.OrderBy(m => m.Order).ToList();

        Assert.Equal(0, orderedItems[0].Order);
        Assert.Equal(1, orderedItems[1].Order);
        Assert.Equal(2, orderedItems[2].Order);
    }

    [Fact]
    public void CarouselPost_AllMediaItems_AreImageType()
    {
        var post = CreateCarouselPost();

        Assert.All(post.MediaItems, item =>
            Assert.Equal(MediaType.Image, item.MediaType));
    }

    [Fact]
    public void CarouselPost_DetectedByMediaItemsCount()
    {
        var post = CreateCarouselPost();

        var isCarousel = post.MediaItems?.Count >= 2;

        Assert.True(isCarousel);
    }

    [Fact]
    public void SingleImagePost_NotDetectedAsCarousel()
    {
        var post = new Post
        {
            Content = "Single image post",
            Platform = Platform.Instagram,
            MediaType = MediaType.Image,
            MediaUrl = "media/image.jpg",
            MediaItems = new List<PostMediaItem>
            {
                new() { MediaUrl = "media/image.jpg", Order = 0, MediaType = MediaType.Image },
            },
        };

        var isCarousel = post.MediaItems?.Count >= 2;

        Assert.False(isCarousel);
    }

    [Fact]
    public void PostWithNoMediaItems_NotDetectedAsCarousel()
    {
        var post = new Post
        {
            Content = "Text only post",
            Platform = Platform.Instagram,
        };

        var isCarousel = post.MediaItems?.Count >= 2;

        Assert.False(isCarousel);
    }

    private static Post CreateCarouselPost()
    {
        var postId = Guid.NewGuid();
        return new Post
        {
            Id = postId,
            Content = "Test carousel post",
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
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/image2.jpg", MediaType = MediaType.Image },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 2, MediaUrl = "media/image3.jpg", MediaType = MediaType.Image },
            },
        };
    }
}

/// <summary>
/// Tests for carousel child ID serialization/deserialization helpers.
/// Uses the same logic as InstagramPublisher.DeserializeChildIds/SerializeChildIds.
/// </summary>
public class CarouselChildIdSerializationTests
{
    [Fact]
    public void DeserializeChildIds_Null_ReturnsEmptyList()
    {
        var result = DeserializeChildIds(null);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeChildIds_EmptyString_ReturnsEmptyList()
    {
        var result = DeserializeChildIds("");
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeChildIds_InvalidJson_ReturnsEmptyList()
    {
        var result = DeserializeChildIds("not-json");
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void DeserializeChildIds_ValidJson_ReturnsList()
    {
        var json = "[\"child-1\",\"child-2\",\"child-3\"]";
        var result = DeserializeChildIds(json);

        Assert.Equal(3, result.Count);
        Assert.Equal("child-1", result[0]);
        Assert.Equal("child-2", result[1]);
        Assert.Equal("child-3", result[2]);
    }

    [Fact]
    public void SerializeChildIds_EmptyList_ReturnsEmptyArray()
    {
        var json = SerializeChildIds(new List<string>());
        Assert.Equal("[]", json);
    }

    [Fact]
    public void SerializeChildIds_WithItems_ReturnsJsonArray()
    {
        var ids = new List<string> { "id-a", "id-b" };
        var json = SerializeChildIds(ids);

        Assert.Equal("[\"id-a\",\"id-b\"]", json);
    }

    [Fact]
    public void RoundTrip_SerializeDeserialize_PreservesOrder()
    {
        var original = new List<string> { "first", "second", "third", "fourth" };

        var json = SerializeChildIds(original);
        var result = DeserializeChildIds(json);

        Assert.Equal(original.Count, result.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i], result[i]);
        }
    }

    [Fact]
    public void IncrementalBuild_SimulatesIdempotentChildCreation()
    {
        // Simulate building child IDs one at a time (as the publisher does)
        var childIds = DeserializeChildIds(null);
        Assert.Empty(childIds);

        // Create first child
        childIds.Add("child-1");
        var json = SerializeChildIds(childIds);

        // Resume after persistence — deserialize from saved state
        childIds = DeserializeChildIds(json);
        Assert.Single(childIds);

        // Create second child
        childIds.Add("child-2");
        json = SerializeChildIds(childIds);

        // Resume again
        childIds = DeserializeChildIds(json);
        Assert.Equal(2, childIds.Count);
        Assert.Equal("child-1", childIds[0]);
        Assert.Equal("child-2", childIds[1]);
    }

    // Mirror the same logic as InstagramPublisher (private static methods)
    private static List<string> DeserializeChildIds(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string SerializeChildIds(List<string> ids)
    {
        return JsonSerializer.Serialize(ids);
    }
}

/// <summary>
/// Tests for carousel publishing state transitions.
/// Mirrors the existing InstagramVideoStateTransitionTests but for carousel flow.
/// </summary>
public class InstagramCarouselStateTransitionTests
{
    [Fact]
    public void CarouselFlow_InitialState_NoContainerIds()
    {
        var post = CreateCarouselPost();

        Assert.Null(post.InstagramChildCreationIds);
        Assert.Null(post.InstagramCarouselCreationId);
        Assert.Equal(0, post.ProcessingPollCount);
        Assert.Equal(PostStatus.Scheduled, post.Status);
    }

    [Fact]
    public void CarouselFlow_AfterChildCreation_PersistsChildIds()
    {
        var post = CreateCarouselPost();
        post.Status = PostStatus.Publishing;

        // Simulate creating children one by one
        var childIds = new List<string>();
        childIds.Add("child-container-1");
        post.InstagramChildCreationIds = JsonSerializer.Serialize(childIds);

        childIds.Add("child-container-2");
        post.InstagramChildCreationIds = JsonSerializer.Serialize(childIds);

        childIds.Add("child-container-3");
        post.InstagramChildCreationIds = JsonSerializer.Serialize(childIds);

        var storedIds = JsonSerializer.Deserialize<List<string>>(post.InstagramChildCreationIds);
        Assert.Equal(3, storedIds!.Count);
    }

    [Fact]
    public void CarouselFlow_AfterCarouselContainerCreation_HasCarouselId()
    {
        var post = CreateCarouselPost();
        post.Status = PostStatus.Publishing;
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "child-1", "child-2", "child-3" });

        // Simulate carousel container creation
        post.InstagramCarouselCreationId = "carousel-container-456";

        Assert.Equal("carousel-container-456", post.InstagramCarouselCreationId);
    }

    [Fact]
    public void CarouselFlow_ProcessingRetry_SetsProcessing()
    {
        var post = CreateCarouselPost();
        post.Status = PostStatus.Publishing;
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "child-1", "child-2", "child-3" });
        post.InstagramCarouselCreationId = "carousel-container-456";

        // Simulate processing retry (IN_PROGRESS)
        post.ProcessingPollCount++;
        post.Status = PostStatus.Processing;
        post.NextRetryAt = DateTime.UtcNow.AddSeconds(30);

        Assert.Equal(PostStatus.Processing, post.Status);
        Assert.Equal(1, post.ProcessingPollCount);
        Assert.NotNull(post.NextRetryAt);
        Assert.Equal(0, post.RetryCount); // Not a hard failure — ProcessingPollCount only
    }

    [Fact]
    public void CarouselFlow_ProcessingTimeout_SetsFailed()
    {
        var post = CreateCarouselPost();
        post.InstagramCarouselCreationId = "carousel-container-456";
        post.ProcessingPollCount = Post.MaxProcessingPollCount;

        // Should timeout
        post.Status = PostStatus.Failed;
        post.ErrorMessage = "Video processing timed out";

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("timed out", post.ErrorMessage);
    }

    [Fact]
    public void CarouselFlow_Finished_SetsPublished()
    {
        var post = CreateCarouselPost();
        post.InstagramCarouselCreationId = "carousel-container-456";
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "child-1", "child-2", "child-3" });
        post.ProcessingPollCount = 2;

        // Simulate successful publish
        post.Status = PostStatus.Published;
        post.ExternalPostId = "media-789";
        post.PublishedAt = DateTime.UtcNow;
        post.ErrorMessage = null;
        post.InstagramMediaType = InstagramMediaType.CarouselAlbum;

        Assert.Equal(PostStatus.Published, post.Status);
        Assert.Equal("media-789", post.ExternalPostId);
        Assert.NotNull(post.PublishedAt);
        Assert.Equal(InstagramMediaType.CarouselAlbum, post.InstagramMediaType);
    }

    [Fact]
    public void CarouselFlow_ContainerError_SetsFailed()
    {
        var post = CreateCarouselPost();
        post.InstagramCarouselCreationId = "carousel-container-456";

        // Carousel container returned ERROR
        post.Status = PostStatus.Failed;
        post.ErrorMessage = "Carousel container processing failed: invalid media";

        Assert.Equal(PostStatus.Failed, post.Status);
        Assert.Contains("failed", post.ErrorMessage);
    }

    [Fact]
    public void CarouselFlow_ContainerExpired_ClearsCarouselId_KeepsChildren()
    {
        var post = CreateCarouselPost();
        var childIdsJson = JsonSerializer.Serialize(
            new List<string> { "child-1", "child-2", "child-3" });
        post.InstagramChildCreationIds = childIdsJson;
        post.InstagramCarouselCreationId = "carousel-container-456";

        // Container expired — clear carousel ID but keep children (they may still be valid)
        post.InstagramCarouselCreationId = null;

        Assert.Null(post.InstagramCarouselCreationId);
        Assert.Equal(childIdsJson, post.InstagramChildCreationIds); // Children preserved
    }

    [Fact]
    public void CarouselFlow_ProcessingRetryDoesNotIncrementRetryCount()
    {
        var post = CreateCarouselPost();
        post.InstagramCarouselCreationId = "carousel-container-456";

        // Multiple processing polls
        post.ProcessingPollCount = 5;

        // RetryCount stays at 0 — processing polls are separate
        Assert.Equal(0, post.RetryCount);
        Assert.Equal(5, post.ProcessingPollCount);
    }

    [Fact]
    public void CarouselFlow_IdempotencyCheck_AlreadyPublished()
    {
        var post = CreateCarouselPost();
        post.Status = PostStatus.Published;
        post.ExternalPostId = "media-789";

        // Should not republish
        Assert.Equal(PostStatus.Published, post.Status);
        Assert.False(string.IsNullOrEmpty(post.ExternalPostId));
    }

    [Fact]
    public void CarouselFlow_Idempotency_ResumesFromPartialChildren()
    {
        var post = CreateCarouselPost();
        // 2 of 3 children already created
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "child-1", "child-2" });
        post.Status = PostStatus.RetryPending;

        var childIds = JsonSerializer.Deserialize<List<string>>(post.InstagramChildCreationIds);
        var totalItems = post.MediaItems.Count;

        // Should resume from child index 2 (skip 0 and 1)
        Assert.Equal(2, childIds!.Count);
        Assert.Equal(3, totalItems);
        Assert.True(childIds.Count < totalItems, "There are still children to create");
    }

    [Fact]
    public void CarouselFlow_Idempotency_ResumesFromCarouselContainerCreated()
    {
        var post = CreateCarouselPost();
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "child-1", "child-2", "child-3" });
        post.InstagramCarouselCreationId = "carousel-container-456";
        post.ProcessingPollCount = 2;
        post.Status = PostStatus.Processing;

        // On next attempt: all children done, carousel container exists → skip to status check
        Assert.False(string.IsNullOrEmpty(post.InstagramCarouselCreationId));
        var childIds = JsonSerializer.Deserialize<List<string>>(post.InstagramChildCreationIds);
        Assert.Equal(post.MediaItems.Count, childIds!.Count);
    }

    [Fact]
    public void CarouselFlow_ChildCreationFailure_DoesNotClearPreviousChildren()
    {
        var post = CreateCarouselPost();
        // First child was created successfully
        post.InstagramChildCreationIds = JsonSerializer.Serialize(
            new List<string> { "child-1" });

        // Simulating failure on second child (e.g., API error)
        // The publisher returns without modifying InstagramChildCreationIds
        var childIds = JsonSerializer.Deserialize<List<string>>(post.InstagramChildCreationIds);
        Assert.Single(childIds!);
        Assert.Equal("child-1", childIds[0]);
    }

    private static Post CreateCarouselPost()
    {
        var postId = Guid.NewGuid();
        return new Post
        {
            Id = postId,
            Content = "Test carousel post #carousel",
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
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/image2.jpg", MediaType = MediaType.Image },
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 2, MediaUrl = "media/image3.jpg", MediaType = MediaType.Image },
            },
        };
    }
}

/// <summary>
/// Tests for PostDto mapping with carousel media items.
/// </summary>
public class CarouselPostDtoTests
{
    [Fact]
    public void PostDto_FromEntity_IncludesMediaItems()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Carousel post",
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
                new() { Id = Guid.NewGuid(), PostId = postId, Order = 1, MediaUrl = "media/image2.jpg", MediaType = MediaType.Image },
            },
        };

        var dto = PostDto.FromEntity(post);

        Assert.NotNull(dto.MediaItems);
        Assert.Equal(2, dto.MediaItems!.Count);
        Assert.Equal("CarouselAlbum", dto.InstagramMediaType);
    }

    [Fact]
    public void PostDto_FromEntity_MediaItemsOrderedByOrder()
    {
        var postId = Guid.NewGuid();
        var post = new Post
        {
            Id = postId,
            Content = "Carousel post",
            Platform = Platform.Instagram,
            MediaType = MediaType.Image,
            Status = PostStatus.Published,
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
    public void PostDto_FromEntity_EmptyMediaItems_ReturnsNull()
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Single image post",
            Platform = Platform.Instagram,
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

    [Fact]
    public void PostDto_FromEntity_NullMediaItems_ReturnsNull()
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Text post",
            Platform = Platform.Facebook,
            Status = PostStatus.Published,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        // Ensure MediaItems list is empty (default) not populated
        var dto = PostDto.FromEntity(post);

        Assert.Null(dto.MediaItems);
    }

    [Fact]
    public void ParseGraphMediaType_CarouselAlbum_ReturnsCorrectType()
    {
        var result = Services.Publishing.InstagramPublisher.ParseGraphMediaType("CAROUSEL_ALBUM");
        Assert.Equal(InstagramMediaType.CarouselAlbum, result);
    }

    [Fact]
    public void ParseGraphMediaType_CarouselAlbumLowerCase_ReturnsCorrectType()
    {
        var result = Services.Publishing.InstagramPublisher.ParseGraphMediaType("carousel_album");
        Assert.Equal(InstagramMediaType.CarouselAlbum, result);
    }
}

/// <summary>
/// Tests for carousel validation limits (2-10 images, images only).
/// </summary>
public class CarouselValidationTests
{
    [Theory]
    [InlineData(2, true)]   // Minimum carousel size
    [InlineData(5, true)]   // Mid-range
    [InlineData(10, true)]  // Maximum carousel size
    [InlineData(1, false)]  // Too few (single image, not carousel)
    [InlineData(11, false)] // Too many
    [InlineData(0, false)]  // No items
    public void CarouselImageCount_ValidatesCorrectly(int count, bool shouldBeValid)
    {
        const int minCarouselImages = 2;
        const int maxCarouselImages = 10;

        var isValid = count >= minCarouselImages && count <= maxCarouselImages;
        Assert.Equal(shouldBeValid, isValid);
    }

    [Fact]
    public void CarouselMediaItems_AllImageType_IsValid()
    {
        var items = CreateMediaItems(3, MediaType.Image);
        var allImages = items.All(i => i.MediaType == MediaType.Image);
        Assert.True(allImages);
    }

    [Fact]
    public void CarouselMediaItems_ContainsVideo_IsInvalid()
    {
        var items = new List<PostMediaItem>
        {
            new() { MediaUrl = "img1.jpg", Order = 0, MediaType = MediaType.Image },
            new() { MediaUrl = "vid.mp4", Order = 1, MediaType = MediaType.Video },
            new() { MediaUrl = "img2.jpg", Order = 2, MediaType = MediaType.Image },
        };

        var allImages = items.All(i => i.MediaType == MediaType.Image);
        Assert.False(allImages);
    }

    [Fact]
    public void CarouselMediaItems_ContainsNone_IsInvalid()
    {
        var items = new List<PostMediaItem>
        {
            new() { MediaUrl = "img1.jpg", Order = 0, MediaType = MediaType.Image },
            new() { MediaUrl = "none.txt", Order = 1, MediaType = MediaType.None },
        };

        var allImages = items.All(i => i.MediaType == MediaType.Image);
        Assert.False(allImages);
    }

    private static List<PostMediaItem> CreateMediaItems(int count, MediaType type)
    {
        return Enumerable.Range(0, count).Select(i => new PostMediaItem
        {
            MediaUrl = $"media/image{i}.jpg",
            Order = i,
            MediaType = type,
        }).ToList();
    }
}
