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
/// Publisher preflight tests for the Story no-text rule. A stored Story row carrying non-empty
/// Content (a legacy row, or one written through an older/crafted client) must be refused by
/// <c>GuardStoryPreflightAsync</c> BEFORE any Meta request — the HttpClient throws if ever used,
/// so a passing "blocked" test also proves Meta was never contacted. Same internal-guard test
/// pattern as <see cref="PublisherMediaGuardTests"/> (the full PublishAsync path uses
/// ExecuteUpdateAsync, which the EF InMemory provider does not support). This preflight is what
/// the worker and publish-now both route through, so it also covers those paths for stored rows.
/// </summary>
public class StoryPublisherContentGuardTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000c9");

    private readonly AppDbContext _db;

    public StoryPublisherContentGuardTests()
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

    private FacebookStoryPublisher BuildFbStoryPublisher(ILogger<FacebookStoryPublisher>? logger = null)
        => new(
            _db,
            Mock.Of<IPostScheduler>(),
            Mock.Of<IMediaService>(),
            ThrowingHttpClient(),
            logger ?? NullLogger<FacebookStoryPublisher>.Instance,
            Mock.Of<IProviderConnectionService>(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            new PassThroughMediaGate());

    private InstagramStoryPublisher BuildIgStoryPublisher(ILogger<InstagramStoryPublisher>? logger = null)
        => new(
            _db,
            Mock.Of<IPostScheduler>(),
            Mock.Of<IMediaService>(),
            ThrowingHttpClient(),
            logger ?? NullLogger<InstagramStoryPublisher>.Instance,
            Mock.Of<IProviderConnectionService>(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            new PassThroughMediaGate());

    private static Post StoryPost(Platform platform, string content) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Ws,
        Content = content,
        Platform = platform,
        PostType = PostType.Story,
        MediaType = MediaType.Image,
        MediaUrl = "users/u/workspaces/w/providers/meta/media/m/story.jpg",
        Status = PostStatus.Scheduled,
        ScheduledAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // ── Facebook Story: stored text blocked before Meta ─────────────────────────

    [Theory]
    [InlineData("hello")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("  \t\n ")]
    public async Task FacebookStory_StoredText_BlockedBeforeMeta(string content)
    {
        var publisher = BuildFbStoryPublisher();
        var post = StoryPost(Platform.Facebook, content);

        var error = await publisher.GuardStoryPreflightAsync(post, CancellationToken.None);

        // Blocked with the placement-specific message; by construction no Meta call was
        // possible (the HttpClient throws if ever used).
        Assert.Equal("Facebook Story posts do not support post text.", error);
    }

    [Fact]
    public async Task FacebookStory_EmptyContent_PassesPreflight()
    {
        var publisher = BuildFbStoryPublisher();
        var post = StoryPost(Platform.Facebook, string.Empty);

        var error = await publisher.GuardStoryPreflightAsync(post, CancellationToken.None);

        Assert.Null(error);
    }

    [Fact]
    public async Task FacebookFeedRow_IsNotBlockedByStoryTextGuard()
    {
        // Defense: the text rule keys off the stored row's real PostType, never a global
        // "stories have no text" assumption — a Feed row with text passes this guard.
        var publisher = BuildFbStoryPublisher();
        var post = StoryPost(Platform.Facebook, "feed text");
        post.PostType = PostType.Feed;

        var error = await publisher.GuardStoryPreflightAsync(post, CancellationToken.None);

        Assert.Null(error);
    }

    // ── Instagram Story: stored text blocked before Meta ────────────────────────

    [Theory]
    [InlineData("hello")]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("  \t\n ")]
    public async Task InstagramStory_StoredText_BlockedBeforeMeta(string content)
    {
        var publisher = BuildIgStoryPublisher();
        var post = StoryPost(Platform.Instagram, content);

        var error = await publisher.GuardStoryPreflightAsync(post, CancellationToken.None);

        Assert.Equal("Instagram Story posts do not support captions.", error);
    }

    [Fact]
    public async Task InstagramStory_EmptyContent_PassesPreflight()
    {
        var publisher = BuildIgStoryPublisher();
        var post = StoryPost(Platform.Instagram, string.Empty);

        var error = await publisher.GuardStoryPreflightAsync(post, CancellationToken.None);

        Assert.Null(error);
    }

    // ── Log hygiene: the hidden text itself is never logged ─────────────────────

    [Fact]
    public async Task BlockedStoryText_IsNeverWrittenToLogs()
    {
        var fbLogger = new CapturingLogger<FacebookStoryPublisher>();
        var igLogger = new CapturingLogger<InstagramStoryPublisher>();
        const string hidden = "SECRET_HIDDEN_STORY_TEXT";

        await BuildFbStoryPublisher(fbLogger)
            .GuardStoryPreflightAsync(StoryPost(Platform.Facebook, hidden), CancellationToken.None);
        await BuildIgStoryPublisher(igLogger)
            .GuardStoryPreflightAsync(StoryPost(Platform.Instagram, hidden), CancellationToken.None);

        Assert.NotEmpty(fbLogger.Messages); // the block IS logged (without the content)
        Assert.NotEmpty(igLogger.Messages);
        Assert.DoesNotContain(fbLogger.Messages, m => m.Contains(hidden));
        Assert.DoesNotContain(igLogger.Messages, m => m.Contains(hidden));
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    private sealed class ThrowOnSendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Meta API must NOT be called when a story fails the pre-publish preflight.");
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
