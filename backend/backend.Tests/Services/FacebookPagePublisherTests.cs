using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services;

public class FacebookPagePublisherTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly Mock<IPostScheduler> _schedulerMock;
    private readonly Mock<IMediaService> _mediaServiceMock;
    private readonly FeatureSettings _featureSettings;
    private readonly MetaApiOptions _metaApiOptions;
    private readonly PublishingOptions _publishingOptions;
    private readonly Mock<ILogger<FacebookPagePublisher>> _loggerMock;

    public FacebookPagePublisherTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _dbContext = new AppDbContext(options);

        _schedulerMock = new Mock<IPostScheduler>();
        _schedulerMock.Setup(s => s.ScheduleRetryAsync(It.IsAny<Post>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduleResult(true));

        _mediaServiceMock = new Mock<IMediaService>();
        _mediaServiceMock.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(url => url != null && !url.StartsWith("http"));
        _mediaServiceMock.Setup(m => m.GenerateDownloadUrl(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .Returns<string, TimeSpan>((key, _) => $"https://storage.example.com/{key}?signed=1");

        _featureSettings = new FeatureSettings();
        _metaApiOptions = new MetaApiOptions();
        _publishingOptions = new PublishingOptions
        {
            WorkerPollIntervalSeconds = 30,
            StuckPostThresholdMinutes = 5,
            StuckPostRetryDelaySeconds = 10,
            MediaDownloadUrlExpirationMinutes = 60,
            VideoDownloadUrlExpirationMinutes = 120,
            ImagePollMaxAttempts = 30,
            ImagePollIntervalSeconds = 2,
            OAuthStateExpirationMinutes = 10
        };
        _loggerMock = new Mock<ILogger<FacebookPagePublisher>>();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    // ──────────────────────────────────────────────
    //  Helpers
    // ──────────────────────────────────────────────

    private FacebookPagePublisher CreatePublisher(HttpClient httpClient)
    {
        // Real provider connection service + Meta handler so the Auth-error path
        // actually flags the connection ReauthRequired against this DbContext.
        var metaHandler = new MetaProviderLifecycleHandler(
            _dbContext, _schedulerMock.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        var providerConnections = new ProviderConnectionService(
            _dbContext, new[] { (IProviderLifecycleHandler)metaHandler },
            NullLogger<ProviderConnectionService>.Instance);

        return new FacebookPagePublisher(
            _dbContext,
            _schedulerMock.Object,
            _mediaServiceMock.Object,
            _featureSettings,
            httpClient,
            _loggerMock.Object,
            providerConnections,
            _metaApiOptions,
            _publishingOptions);
    }

    private Post CreateMultiPhotoPost(int imageCount, string content = "Hello FB!")
    {
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            MetaConnectionId = Guid.NewGuid(),
            PageId = "PAGE123",
            Name = "Test Page",
            AccessToken = "PAGE_TOKEN_abc123",
        };

        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = content,
            Platform = Platform.Facebook,
            MediaType = MediaType.Image,
            MediaUrl = "https://example.com/image1.jpg",
            TargetPage = page,
            TargetPageId = page.Id,
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        post.MediaItems = Enumerable.Range(0, imageCount)
            .Select(i => new PostMediaItem
            {
                Id = Guid.NewGuid(),
                PostId = post.Id,
                Order = i,
                MediaUrl = $"https://example.com/image{i + 1}.jpg",
                MediaType = MediaType.Image,
            })
            .ToList();

        _dbContext.Set<ConnectedPage>().Add(page);
        _dbContext.Posts.Add(post);
        _dbContext.SaveChanges();

        return post;
    }

    /// <summary>
    /// Builds an HttpClient backed by a fake handler that returns pre-configured responses.
    /// </summary>
    private static HttpClient CreateMockHttpClient(Queue<HttpResponseMessage> responses)
    {
        var handler = new FakeHttpHandler(responses);
        return new HttpClient(handler);
    }

    /// <summary>
    /// Builds a successful /photos response with the given photo ID.
    /// </summary>
    private static HttpResponseMessage PhotoUploadSuccess(string photoId)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = photoId })),
        };
    }

    /// <summary>
    /// Builds a failed /photos response.
    /// </summary>
    private static HttpResponseMessage PhotoUploadFailure(int code = 100, string message = "Upload failed")
    {
        var body = new
        {
            error = new
            {
                message,
                code,
                type = "OAuthException",
            }
        };
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(body)),
        };
    }

    /// <summary>
    /// Builds a successful /feed response with the given post ID.
    /// </summary>
    private static HttpResponseMessage FeedPostSuccess(string postId)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { id = postId })),
        };
    }

    // ──────────────────────────────────────────────
    //  HAPPY PATH tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task MultiPhoto_HappyPath_2Images_WithCaption_PublishesWithAttachedMedia()
    {
        // Arrange: 2 photos upload OK, then /feed OK
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadSuccess("PHOTO_ID_1"));
        responses.Enqueue(PhotoUploadSuccess("PHOTO_ID_2"));
        responses.Enqueue(FeedPostSuccess("PAGE123_9999"));

        var httpClient = CreateMockHttpClient(responses);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2, "My caption");

        // Act — call CallMetaApiAsync directly to bypass TryClaimPostAsync (uses ExecuteUpdateAsync unsupported by InMemory)
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("PAGE123_9999", result.ExternalPostId);
        Assert.Empty(responses); // all consumed
    }

    [Fact]
    public async Task MultiPhoto_HappyPath_3Images_WithCaption_PublishesWithAttachedMedia()
    {
        // Arrange: 3 photos upload OK, then /feed OK
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadSuccess("PH_A"));
        responses.Enqueue(PhotoUploadSuccess("PH_B"));
        responses.Enqueue(PhotoUploadSuccess("PH_C"));
        responses.Enqueue(FeedPostSuccess("PAGE123_FEED_1"));

        var httpClient = CreateMockHttpClient(responses);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(3, "Three images!");

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("PAGE123_FEED_1", result.ExternalPostId);
        Assert.Empty(responses);
    }

    [Fact]
    public async Task MultiPhoto_EmptyCaption_SendsSingleSpace()
    {
        // Arrange: 2 photos + feed call
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadSuccess("PH_1"));
        responses.Enqueue(PhotoUploadSuccess("PH_2"));
        responses.Enqueue(FeedPostSuccess("PAGE123_EMPTY_MSG"));

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2, "");  // empty caption

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.True(result.Success);

        // Verify the feed call (3rd request) contained message with a space (not empty)
        var feedRequest = handler.SentRequests[2];
        var feedBody = await feedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("message=", feedBody);
        // The space should be URL-encoded as + or %20
        Assert.DoesNotContain("message=&", feedBody);  // not empty
    }

    // ──────────────────────────────────────────────
    //  FAILURE PATH tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task MultiPhoto_FirstPhotoUploadFails_FeedNotCalled()
    {
        // Arrange: first photo upload fails, no feed call expected
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadFailure(100, "Invalid image URL"));
        // No /feed response queued — should NOT be reached

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2);

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.False(result.Success);

        // Only 1 HTTP request should have been made (the failed photo upload)
        Assert.Single(handler.SentRequests);
        Assert.Contains("/photos", handler.SentRequests[0].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task MultiPhoto_SecondPhotoUploadFails_FeedNotCalled()
    {
        // Arrange: first photo OK, second fails
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadSuccess("PH_OK"));
        responses.Enqueue(PhotoUploadFailure(2, "Service temporarily unavailable"));
        // No /feed response queued

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(3);

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.False(result.Success);

        // Only 2 HTTP requests: 1st photo OK, 2nd photo failed. No /feed call.
        Assert.Equal(2, handler.SentRequests.Count);
        Assert.DoesNotContain(handler.SentRequests, r => r.RequestUri!.PathAndQuery.Contains("/feed"));
    }

    [Fact]
    public async Task MultiPhoto_FailedUpload_TransientError_ResultsInTransientErrorType()
    {
        // Arrange: photo upload fails with transient error code
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadFailure(2, "Service temporarily unavailable")); // code 2 = transient

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2);

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(PublishErrorType.Transient, result.ErrorType);
    }

    [Fact]
    public async Task MultiPhoto_FailedUpload_PermanentError_ResultsInPermanentErrorType()
    {
        // Arrange: photo upload fails with a true permanent (content) error code.
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadFailure(100, "Invalid parameter")); // code 100 = permanent

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2);

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(PublishErrorType.Permanent, result.ErrorType);
    }

    [Fact]
    public async Task MultiPhoto_FailedUpload_TokenExpired_ResultsInAuthErrorType()
    {
        // Code 190 (access token expired/invalid) now classifies as Auth so the
        // publisher flags the workspace connection ReauthRequired without disconnecting.
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadFailure(190, "Access token expired"));

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2);

        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PublishErrorType.Auth, result.ErrorType);
    }

    // ──────────────────────────────────────────────
    //  PHOTO ID PARSING tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task UploadUnpublishedPhoto_ParsesIdField_NotPostId()
    {
        // The /photos endpoint returns {"id":"12345"}, NOT {"post_id":"12345"}.
        // We must parse "id", not "post_id".
        var responses = new Queue<HttpResponseMessage>();
        // Return a response with ONLY "id" field
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"PHOTO_99\"}"),
        });
        responses.Enqueue(PhotoUploadSuccess("PHOTO_100"));
        responses.Enqueue(FeedPostSuccess("FEED_POST_1"));

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2);

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert: if we parsed "id" correctly, both photos uploaded and feed call succeeded
        Assert.True(result.Success);
        Assert.Equal(3, handler.SentRequests.Count);
    }

    // ──────────────────────────────────────────────
    //  FEED CALL FORMAT tests
    // ──────────────────────────────────────────────

    [Fact]
    public async Task MultiPhoto_FeedCall_HasAttachedMediaKeys()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadSuccess("ID_A"));
        responses.Enqueue(PhotoUploadSuccess("ID_B"));
        responses.Enqueue(FeedPostSuccess("FEED_123"));

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2, "With photos");

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.True(result.Success);

        // Verify the /feed request body contains attached_media keys with UN-ENCODED brackets
        var feedRequest = handler.SentRequests[2];
        var feedBody = await feedRequest.Content!.ReadAsStringAsync();

        Assert.Contains("attached_media[0]=", feedBody);
        Assert.Contains("attached_media[1]=", feedBody);
        Assert.Contains("media_fbid", feedBody);
        Assert.Contains("ID_A", feedBody);
        Assert.Contains("ID_B", feedBody);

        // Verify brackets are NOT percent-encoded in keys
        Assert.DoesNotContain("attached_media%5B", feedBody);
    }

    [Fact]
    public async Task MultiPhoto_FeedCall_UsesFormUrlEncoded_ContentType()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadSuccess("P1"));
        responses.Enqueue(PhotoUploadSuccess("P2"));
        responses.Enqueue(FeedPostSuccess("F1"));

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2);

        // Act
        await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert: /feed request uses form-urlencoded
        var feedRequest = handler.SentRequests[2];
        Assert.Contains("application/x-www-form-urlencoded",
            feedRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task MultiPhoto_PhotoUpload_UsesFormUrlEncoded_WithPublishedFalse()
    {
        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(PhotoUploadSuccess("P1"));
        responses.Enqueue(PhotoUploadSuccess("P2"));
        responses.Enqueue(FeedPostSuccess("F1"));

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);
        var post = CreateMultiPhotoPost(2);

        // Act
        await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert: /photos requests use form-urlencoded with published=false
        for (int i = 0; i < 2; i++)
        {
            var photoRequest = handler.SentRequests[i];
            Assert.Contains("/photos", photoRequest.RequestUri!.PathAndQuery);
            var body = await photoRequest.Content!.ReadAsStringAsync();
            Assert.Contains("published=false", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ──────────────────────────────────────────────
    //  GUARD tests (CreateFeedPostWithAttachedMediaAsync directly)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task CreateFeedPost_WithEmptyPhotoIds_ReturnsFailure()
    {
        var httpClient = CreateMockHttpClient(new Queue<HttpResponseMessage>());
        var publisher = CreatePublisher(httpClient);

        // Call the internal method directly with empty list
        var result = await publisher.CreateFeedPostWithAttachedMediaAsync(
            "PAGE123", "caption", new List<string>(), "TOKEN", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PublishErrorType.Permanent, result.ErrorType);
        Assert.Contains("without any uploaded photos", result.ErrorMessage);
    }

    [Fact]
    public async Task CreateFeedPost_WithNullPhotoIds_ReturnsFailure()
    {
        var httpClient = CreateMockHttpClient(new Queue<HttpResponseMessage>());
        var publisher = CreatePublisher(httpClient);

        var result = await publisher.CreateFeedPostWithAttachedMediaAsync(
            "PAGE123", "caption", null!, "TOKEN", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PublishErrorType.Permanent, result.ErrorType);
    }

    // ──────────────────────────────────────────────
    //  Existing flow tests (single photo / text-only unchanged)
    // ──────────────────────────────────────────────

    [Fact]
    public async Task SingleImage_StillUsesPhotosEndpoint()
    {
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            MetaConnectionId = Guid.NewGuid(),
            PageId = "SINGLE_PAGE",
            Name = "Test",
            AccessToken = "TOKEN",
        };
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Single image post",
            Platform = Platform.Facebook,
            MediaType = MediaType.Image,
            MediaUrl = "https://example.com/image.jpg",
            TargetPage = page,
            TargetPageId = page.Id,
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"SINGLE_PHOTO_POST\"}"),
        });

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);

        // Act — call CallMetaApiAsync directly
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Single(handler.SentRequests);
        Assert.Contains("/photos", handler.SentRequests[0].RequestUri!.PathAndQuery);
        Assert.DoesNotContain("/feed", handler.SentRequests[0].RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task TextOnly_StillUsesFeedEndpoint()
    {
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            MetaConnectionId = Guid.NewGuid(),
            PageId = "TXT_PAGE",
            Name = "Test",
            AccessToken = "TOKEN",
        };
        var post = new Post
        {
            Id = Guid.NewGuid(),
            Content = "Just text",
            Platform = Platform.Facebook,
            MediaType = MediaType.None,
            TargetPage = page,
            TargetPageId = page.Id,
            Status = PostStatus.Scheduled,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var responses = new Queue<HttpResponseMessage>();
        responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"TXT_POST_1\"}"),
        });

        var handler = new FakeHttpHandler(responses);
        var httpClient = new HttpClient(handler);
        var publisher = CreatePublisher(httpClient);

        // Act
        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        // Assert
        Assert.True(result.Success);
        Assert.Single(handler.SentRequests);
        Assert.Contains("/feed", handler.SentRequests[0].RequestUri!.PathAndQuery);
    }
}

// ──────────────────────────────────────────────
//  Fake HTTP Handler for testing
// ──────────────────────────────────────────────

/// <summary>
/// A delegating handler that returns pre-configured responses in order
/// and records all sent requests for assertion.
/// </summary>
public class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    public List<HttpRequestMessage> SentRequests { get; } = new();

    public FakeHttpHandler(Queue<HttpResponseMessage> responses)
    {
        _responses = responses;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Clone the content so it can be read later in assertions
        var clonedRequest = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content != null)
        {
            var contentBytes = request.Content.ReadAsByteArrayAsync(cancellationToken).Result;
            clonedRequest.Content = new ByteArrayContent(contentBytes);
            if (request.Content.Headers.ContentType != null)
                clonedRequest.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }
        SentRequests.Add(clonedRequest);

        if (_responses.Count == 0)
            throw new InvalidOperationException(
                $"No more mock responses. Request: {request.Method} {request.RequestUri}");

        return Task.FromResult(_responses.Dequeue());
    }
}
