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
using PostPilot.Api.Services.Validation;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests;

/// <summary>
/// Defense-in-depth publisher guard tests. These build the publishers with a REAL
/// <see cref="MediaValidationGate"/> (real ImageSharp validation) and an HttpClient that
/// throws if ever called — so a passing test for the "blocked" cases also proves Meta was
/// never contacted. They also assert log hygiene: the guard never logs raw storage keys.
/// </summary>
public class PublisherMediaGuardTests : IDisposable
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000b7");

    private readonly AppDbContext _db;
    private readonly List<string> _tempFiles = new();

    public PublisherMediaGuardTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
        _db.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private readonly Dictionary<string, string> _keyToPath = new();

    private string SeedImageMedia(string storageKey, string contentType, string format, int width, int height)
    {
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);
        var ext = format == "png" ? ".png" : ".jpg";
        var path = Path.Combine(Path.GetTempPath(), $"guardtest_{Guid.NewGuid():N}{ext}");
        using (var fs = File.Create(path))
        {
            if (format == "png") image.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
            else image.Save(fs, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        }
        _tempFiles.Add(path);
        _keyToPath[storageKey] = path;

        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            StorageProvider = "local-disk",
            Bucket = "",
            StorageKey = storageKey,
            OriginalFileName = Path.GetFileName(path),
            ContentType = contentType,
            SizeBytes = new FileInfo(path).Length,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return storageKey;
    }

    private IMediaService BuildMediaService()
    {
        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>()))
            .Returns<string>(key => Task.FromResult<string?>(_keyToPath.TryGetValue(key, out var p) ? p : null));
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>()));
        // Publishing-URL resolution should never be reached when the guard blocks, but stub it
        // so a regression (guard not firing) surfaces as an HttpClient throw, not an NRE.
        mediaService.Setup(m => m.GetPublishingUrlAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://signed.example.com/object?token=SECRET");
        return mediaService.Object;
    }

    private MediaValidationGate BuildGate(IMediaService mediaService, IVideoMetadataExtractor? videoExtractor = null) =>
        new(_db, mediaService,
            new MediaValidationService(
                new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
                videoExtractor ?? Mock.Of<IVideoMetadataExtractor>(),
                NullLogger<MediaValidationService>.Instance),
            NullLogger<MediaValidationGate>.Instance);

    private static IVideoMetadataExtractor FakeVideo(int width, int height, double durationSeconds,
        string container = "mp4", string videoCodec = "h264", string audioCodec = "aac", double? fps = 30)
    {
        var meta = new VideoMetadata(width, height, durationSeconds, container, videoCodec, audioCodec,
            fps, null, container == "mov" ? "video/quicktime" : "video/mp4");
        var mock = new Mock<IVideoMetadataExtractor>();
        mock.Setup(e => e.ExtractAsync(It.IsAny<string>())).ReturnsAsync(meta);
        return mock.Object;
    }

    private string SeedVideoMedia(string storageKey, string contentType, long sizeBytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"guardtest_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, new byte[] { 0x00 });
        _tempFiles.Add(path);
        _keyToPath[storageKey] = path;
        _db.Media.Add(new Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = Path.GetFileName(path), ContentType = contentType,
            SizeBytes = sizeBytes, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        return storageKey;
    }

    private static HttpClient ThrowingHttpClient() => new(new ThrowOnSendHandler());

    private (MetaConnection conn, ConnectedPage page, ConnectedInstagramAccount ig) SeedIgTarget()
    {
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            Provider = ProviderType.Meta,
            IsConnected = true,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            MetaConnectionId = conn.Id,
            PageId = "PAGE_IG",
            Name = "IG Page",
            AccessToken = "PAGE_TOKEN",
            IsConnected = true,
        };
        var ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            MetaConnectionId = conn.Id,
            PageId = "PAGE_IG",
            PageName = "IG Page",
            IgBusinessId = "IG_BIZ_1",
            Username = "tester",
            IsConnected = true,
        };
        _db.Add(conn); _db.Add(page); _db.Add(ig);
        _db.SaveChanges();
        return (conn, page, ig);
    }

    private InstagramPublisher BuildIgPublisher(IMediaService mediaService, IMediaValidationGate gate, ILogger<InstagramPublisher>? logger = null)
        => new(
            _db,
            Mock.Of<IPostScheduler>(),
            mediaService,
            ThrowingHttpClient(),
            logger ?? NullLogger<InstagramPublisher>.Instance,
            BuildProviderConnections(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            gate);

    private FacebookPagePublisher BuildFbPublisher(
        IMediaService mediaService,
        IMediaValidationGate gate,
        HttpClient? httpClient = null)
        => new(
            _db,
            Mock.Of<IPostScheduler>(),
            mediaService,
            new FeatureSettings(),
            httpClient ?? ThrowingHttpClient(),
            NullLogger<FacebookPagePublisher>.Instance,
            BuildProviderConnections(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            gate);

    private IProviderConnectionService BuildProviderConnections()
    {
        var handler = new MetaProviderLifecycleHandler(_db, Mock.Of<IPostScheduler>(), NullLogger<MetaProviderLifecycleHandler>.Instance);
        return new ProviderConnectionService(_db, new[] { (IProviderLifecycleHandler)handler }, NullLogger<ProviderConnectionService>.Instance);
    }

    private static PublishingOptions BuildPublishingOptions() => new()
    {
        MediaDownloadUrlExpirationMinutes = 60,
        VideoDownloadUrlExpirationMinutes = 120,
        ImagePollMaxAttempts = 1,
        ImagePollIntervalSeconds = 1,
        OAuthStateExpirationMinutes = 10,
    };

    private Post SeedIgImagePost(string storageKey, ConnectedInstagramAccount ig, MediaType mediaType = MediaType.Image)
    {
        var post = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            Content = "caption",
            Platform = Platform.Instagram,
            MediaType = mediaType,
            MediaUrl = storageKey,
            TargetInstagramAccountId = ig.Id,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post);
        _db.SaveChanges();
        return post;
    }

    // NOTE: the publishers' full PublishAsync path uses ExecuteUpdateAsync (TryClaimPostAsync),
    // which the EF InMemory provider does not support — the existing FacebookPagePublisherTests
    // dodge this by calling internal methods directly. We do the same: GuardMediaAsync is the
    // pre-Meta guard, so calling it directly proves both the wiring (publisher → gate) and that
    // a blocking error is produced BEFORE any Graph call. The HttpClient is the throwing handler
    // anyway, so no Meta call is possible from within the guard.

    // ── Instagram: PNG refused before Meta ──────────────────────────────────────

    [Fact]
    public async Task InstagramPublisher_RefusesPngWithoutDerivative_BeforeCallingMeta()
    {
        // Phase 3: a PNG with NO Instagram JPEG derivative must be blocked before any Meta
        // call (a derivative is normally generated at upload; this is the defense-in-depth
        // case where it is missing). PNG WITH a derivative is exercised in
        // InstagramDerivativeGateAndPublisherTests.
        var (_, _, ig) = SeedIgTarget();
        var key = SeedImageMedia("ig-key-png", "image/png", "png", 1080, 1080);
        var post = SeedIgImagePost(key, ig);

        var mediaService = BuildMediaService();
        var publisher = BuildIgPublisher(mediaService, BuildGate(mediaService));

        var guardError = await publisher.GuardMediaAsync(post, Placement.Feed, CancellationToken.None);

        // A clear, blocking failure reason is produced (and, by construction, no Meta call:
        // the guard returns before any HTTP, and the HttpClient throws if ever used).
        Assert.NotNull(guardError);
        Assert.Contains("Instagram-ready JPEG", guardError!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstagramPublisher_AllowsValidJpeg_PastGuard()
    {
        var (_, _, ig) = SeedIgTarget();
        var key = SeedImageMedia("ig-key-jpg", "image/jpeg", "jpeg", 1080, 1080);
        var post = SeedIgImagePost(key, ig);

        var mediaService = BuildMediaService();
        var publisher = BuildIgPublisher(mediaService, BuildGate(mediaService));

        var guardError = await publisher.GuardMediaAsync(post, Placement.Feed, CancellationToken.None);

        Assert.Null(guardError); // valid JPEG is not blocked
    }

    // ── Videos are now guarded before Meta (no longer passed through) ────────────

    [Fact]
    public async Task InstagramPublisher_RefusesInvalidVideo_BeforeCallingMeta()
    {
        var (_, _, ig) = SeedIgTarget();
        var key = SeedVideoMedia("ig-vid-bad", "video/mp4", 10L * 1024 * 1024);
        var post = SeedIgImagePost(key, ig, MediaType.Video);

        var mediaService = BuildMediaService();
        // 181s exceeds the IG feed 180s MVP cap → blocked.
        var publisher = BuildIgPublisher(mediaService, BuildGate(mediaService, FakeVideo(1080, 1080, 181)));

        var guardError = await publisher.GuardMediaAsync(post, Placement.Feed, CancellationToken.None);

        Assert.NotNull(guardError); // blocked before any Meta call (HttpClient throws if used)
    }

    [Fact]
    public async Task InstagramPublisher_AllowsValidVideo_PastGuard()
    {
        var (_, _, ig) = SeedIgTarget();
        var key = SeedVideoMedia("ig-vid-ok", "video/quicktime", 20L * 1024 * 1024);
        var post = SeedIgImagePost(key, ig, MediaType.Video);

        var mediaService = BuildMediaService();
        var publisher = BuildIgPublisher(mediaService,
            BuildGate(mediaService, FakeVideo(1080, 1080, 10, container: "mov", videoCodec: "h264", audioCodec: "aac")));

        var guardError = await publisher.GuardMediaAsync(post, Placement.Feed, CancellationToken.None);

        Assert.Null(guardError);
    }

    [Fact]
    public async Task FacebookPublisher_RefusesInvalidVideo_BeforeCallingMeta()
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta, IsConnected = true };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, MetaConnectionId = conn.Id,
            PageId = "PAGE_FB", Name = "FB Page", AccessToken = "PAGE_TOKEN", IsConnected = true,
        };
        _db.Add(conn); _db.Add(page); _db.SaveChanges();

        var key = SeedVideoMedia("fb-vid-big", "video/mp4", 201L * 1024 * 1024); // > 200MB cap
        var post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, Content = "msg", Platform = Platform.Facebook,
            MediaType = MediaType.Video, MediaUrl = key, TargetPageId = page.Id,
            Status = PostStatus.Scheduled, ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post); _db.SaveChanges();

        var mediaService = BuildMediaService();
        var publisher = BuildFbPublisher(mediaService, BuildGate(mediaService, FakeVideo(1280, 720, 30)));

        var guardError = await publisher.GuardMediaAsync(post, Placement.Feed, CancellationToken.None);

        Assert.NotNull(guardError); // 201MB > 200MB → blocked before Meta
    }

    // ── Facebook: PNG allowed by FB rules ───────────────────────────────────────

    [Fact]
    public async Task FacebookPublisher_AllowsPng_PastGuard()
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta, IsConnected = true };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, MetaConnectionId = conn.Id,
            PageId = "PAGE_FB", Name = "FB Page", AccessToken = "PAGE_TOKEN", IsConnected = true,
        };
        _db.Add(conn); _db.Add(page); _db.SaveChanges();

        var key = SeedImageMedia("fb-key-png", "image/png", "png", 1200, 630);
        var post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, Content = "msg", Platform = Platform.Facebook,
            MediaType = MediaType.Image, MediaUrl = key, TargetPageId = page.Id,
            Status = PostStatus.Scheduled, ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post); _db.SaveChanges();

        var mediaService = BuildMediaService();
        var publisher = BuildFbPublisher(mediaService, BuildGate(mediaService));

        var guardError = await publisher.GuardMediaAsync(post, Placement.Feed, CancellationToken.None);

        // PNG is valid for Facebook → guard must NOT block.
        Assert.Null(guardError);
    }

    // ── Log hygiene: guard never logs raw keys ──────────────────────────────────

    [Fact]
    public async Task FacebookPublisher_Png_UsesOriginalStorageKey()
    {
        var conn = new MetaConnection { Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta, IsConnected = true };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, MetaConnectionId = conn.Id,
            PageId = "PAGE_FB", Name = "FB Page", AccessToken = "PAGE_TOKEN", IsConnected = true,
        };
        _db.Add(conn); _db.Add(page); _db.SaveChanges();

        var originalKey = SeedImageMedia("users/u/workspaces/w/providers/meta-facebook/media/mid/photo.png", "image/png", "png", 1200, 630);
        var post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, Content = "msg", Platform = Platform.Facebook,
            MediaType = MediaType.Image, MediaUrl = originalKey, TargetPageId = page.Id,
            Status = PostStatus.Scheduled, ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(post); _db.SaveChanges();

        string? keyRequestedForPublishing = null;
        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>()))
            .Returns<string>(key => Task.FromResult<string?>(_keyToPath.TryGetValue(key, out var p) ? p : null));
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>()));
        mediaService.Setup(m => m.GetPublishingUrlAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan?, CancellationToken>((key, _, _) => keyRequestedForPublishing = key)
            .ReturnsAsync("https://signed.example/original-photo.png");

        var publisher = BuildFbPublisher(
            mediaService.Object,
            BuildGate(mediaService.Object),
            new HttpClient(new OkMetaHandler()));

        var result = await publisher.CallMetaApiAsync(post, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(originalKey, keyRequestedForPublishing);
    }

    [Fact]
    public async Task GuardBlocking_DoesNotLogRawStorageKeyOrSignedUrl()
    {
        var (_, _, ig) = SeedIgTarget();
        var rawKey = "users/abc/workspaces/def/providers/meta-instagram/media/SENSITIVEKEY123/photo.png";
        var key = SeedImageMedia(rawKey, "image/png", "png", 1080, 1080);
        var post = SeedIgImagePost(key, ig);

        // Capture logs from BOTH the publisher and the gate (the gate is where key redaction
        // happens). We pass the capturing logger to the gate; the publisher uses its own.
        var capturing = new CapturingLogger<MediaValidationGate>();
        var mediaService = BuildMediaService();
        var gate = new MediaValidationGate(
            _db, mediaService,
            new MediaValidationService(
                new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
                Mock.Of<IVideoMetadataExtractor>(),
                NullLogger<MediaValidationService>.Instance),
            capturing);
        var publisher = BuildIgPublisher(mediaService, gate);

        var guardError = await publisher.GuardMediaAsync(post, Placement.Feed, CancellationToken.None);
        Assert.NotNull(guardError); // it blocked (PNG for IG)

        // The full raw key (and its high-entropy middle) must never appear in any log line.
        Assert.DoesNotContain(capturing.Messages, m => m.Contains("SENSITIVEKEY123"));
        Assert.DoesNotContain(capturing.Messages, m => m.Contains(rawKey));
        Assert.DoesNotContain(capturing.Messages, m => m.Contains("token=SECRET"));
    }

    // ── Test doubles ────────────────────────────────────────────────────────────

    private sealed class ThrowOnSendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Meta API must NOT be called when media fails the pre-publish guard.");
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
