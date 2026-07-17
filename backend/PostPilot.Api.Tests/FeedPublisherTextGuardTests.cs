using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Publisher preflight tests for the FEED text-length rule (Facebook Feed 5000, Instagram
/// Feed 2200). A stored row whose Content exceeds its platform's limit (a legacy row, or one
/// written through a bypass) must be refused BEFORE any Meta request — the HttpClient throws
/// if ever used, so a passing "blocked" test also proves Meta was never contacted. Same
/// internal-guard test pattern as <see cref="StoryPublisherContentGuardTests"/> (the full
/// PublishAsync path uses ExecuteUpdateAsync, which the EF InMemory provider does not
/// support). This preflight is what the worker and publish-now both route through, so it also
/// covers those paths for stored rows.
/// </summary>
public class FeedPublisherTextGuardTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    private readonly AppDbContext _db;

    public FeedPublisherTextGuardTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    // ── Builders ────────────────────────────────────────────────────────────────

    private static HttpClient ThrowingHttpClient() => new(new ThrowOnSendHandler());

    private static PublishingOptions BuildPublishingOptions() => new()
    {
        MediaDownloadUrlExpirationMinutes = 60,
        VideoDownloadUrlExpirationMinutes = 120,
        ImagePollMaxAttempts = 1,
        ImagePollIntervalSeconds = 1,
        OAuthStateExpirationMinutes = 10,
    };

    private FacebookPagePublisher BuildFbPublisher(HttpClient? httpClient = null)
        => new(
            _db,
            Mock.Of<IPostScheduler>(),
            Mock.Of<IMediaService>(),
            new FeatureSettings(),
            httpClient ?? ThrowingHttpClient(),
            NullLogger<FacebookPagePublisher>.Instance,
            Mock.Of<IProviderConnectionService>(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            new PassThroughMediaGate());

    private InstagramPublisher BuildIgPublisher(ILogger<InstagramPublisher>? logger = null)
        => new(
            _db,
            Mock.Of<IPostScheduler>(),
            Mock.Of<IMediaService>(),
            ThrowingHttpClient(),
            logger ?? NullLogger<InstagramPublisher>.Instance,
            Mock.Of<IProviderConnectionService>(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            new PassThroughMediaGate());

    private Post SeedFacebookFeedPost(string content)
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta, IsConnected = true };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            MetaConnectionId = conn.Id,
            PageId = "PAGE_FB",
            Name = "FB Page",
            AccessToken = "PAGE_TOKEN",
            IsConnected = true,
        };
        var post = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            Content = content,
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            TargetPageId = page.Id,
            TargetPage = page,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Add(conn); _db.Add(page); _db.Posts.Add(post);
        _db.SaveChanges();
        return post;
    }

    private static Post InstagramFeedPost(string content) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Ws,
        Content = content,
        Platform = Platform.Instagram,
        PostType = PostType.Feed,
        MediaType = MediaType.Image,
        MediaUrl = "users/u/workspaces/w/providers/meta-instagram/media/m/photo.jpg",
        Status = PostStatus.Scheduled,
        ScheduledAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── Facebook feed: oversized stored text blocked before Meta ────────────────

    [Fact]
    public async Task FacebookFeed_StoredTextOneOverLimit_BlockedBeforeMeta()
    {
        // CallMetaApiAsync is the single pre-Meta funnel for the worker, publish-now, and any
        // direct caller. The HttpClient throws if used, so the failure proves no Graph call.
        var publisher = BuildFbPublisher();
        var post = SeedFacebookFeedPost(new string('x', 5001));

        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(PublishErrorType.Permanent, result.ErrorType);
        Assert.Contains("Post text failed validation before publishing", result.ErrorMessage);
        Assert.Contains("Max 5000 characters", result.ErrorMessage);
    }

    [Fact]
    public async Task FacebookFeed_StoredTextAtExactLimit_PassesGuardAndPublishes()
    {
        // Exactly 5000 is publishable: the guard passes and the (stubbed) Meta call succeeds.
        var publisher = BuildFbPublisher(new HttpClient(new OkMetaHandler()));
        var post = SeedFacebookFeedPost(new string('x', 5000));

        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task FacebookFeed_3000CharStoredText_PassesGuard()
    {
        // Placement isolation, publisher side: 3000 chars fits Facebook Feed …
        var publisher = BuildFbPublisher(new HttpClient(new OkMetaHandler()));
        var post = SeedFacebookFeedPost(new string('x', 3000));

        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        Assert.True(result.Success);
    }

    // ── Instagram feed: oversized stored caption blocked before Meta ────────────

    [Fact]
    public void InstagramFeed_StoredCaptionOneOverLimit_IsBlocked()
    {
        var publisher = BuildIgPublisher();

        var error = publisher.GuardText(InstagramFeedPost(new string('x', 2201)));

        Assert.Equal("Text is too long for Instagram. Max 2200 characters.", error);
    }

    [Fact]
    public void InstagramFeed_StoredCaptionAtExactLimit_PassesGuard()
    {
        var publisher = BuildIgPublisher();

        Assert.Null(publisher.GuardText(InstagramFeedPost(new string('x', 2200))));
    }

    [Fact]
    public void InstagramFeed_3000CharStoredCaption_IsBlocked()
    {
        // … while the same 3000 chars is over the Instagram Feed limit.
        var publisher = BuildIgPublisher();

        var error = publisher.GuardText(InstagramFeedPost(new string('x', 3000)));

        Assert.Equal("Text is too long for Instagram. Max 2200 characters.", error);
    }

    // ── Log hygiene: the oversized caption itself is never logged ───────────────

    [Fact]
    public void BlockedInstagramCaption_IsNeverWrittenToLogs()
    {
        var logger = new CapturingLogger<InstagramPublisher>();
        var secret = "SECRET_CAPTION_" + new string('x', 2200);

        var error = BuildIgPublisher(logger).GuardText(InstagramFeedPost(secret));

        Assert.NotNull(error);
        Assert.NotEmpty(logger.Messages); // the block IS logged (without the caption)
        Assert.DoesNotContain(logger.Messages, m => m.Contains("SECRET_CAPTION_"));
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    private sealed class ThrowOnSendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Meta API must NOT be called when post text fails the pre-publish guard.");
    }

    private sealed class OkMetaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"fb-post-id"}"""),
            });
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
