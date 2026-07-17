using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

namespace PostPilot.Api.Tests.Services;

/// <summary>
/// Publisher-preflight tests for the Facebook Story <em>video</em> size (≤50MB) and duration
/// (3–90s) rules. These drive the FULL <see cref="FacebookStoryPublisher.PublishAsync"/> path
/// over SQLite (the claim step uses <c>ExecuteUpdateAsync</c>, unsupported by EF InMemory) with a
/// recording fake Meta transport, and prove the shared media gate blocks an invalid Story video
/// <em>before any Meta HTTP request</em> while a valid boundary video (exactly 90s) reaches the
/// real Meta <c>/video_stories</c> flow. No real Meta request is ever made.
///
/// <para>Companion to <c>FacebookStoryVideoCodecPublisherTests</c> (codec pass-through) and the
/// gate-level boundary tests in <c>MediaValidationGateTests</c>; this file specifically asserts the
/// pre-Meta gating for the new size/duration limits.</para>
/// </summary>
public class FacebookStoryPublisherPreflightTests
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000f2");

    [Fact]
    public async Task FacebookStory_VideoExactly90Seconds_PassesPreflight_AndReachesMetaVideoStoryFlow()
    {
        var (db, mediaFile) = await SeedAsync(sizeBytes: 10L * 1024 * 1024);
        var handler = new RecordingMetaHandler();
        // 90s is the inclusive upper boundary — must pass preflight and publish end-to-end.
        var publisher = BuildPublisher(db, mediaFile, handler, durationSeconds: 90, out var postId);

        try
        {
            var result = await publisher.PublishAsync(postId, CancellationToken.None);

            Assert.True(result.Success, $"expected publish success; got: {result.ErrorMessage}");
            // The publisher reached the Meta video-story START request (i.e. preflight passed).
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Post
                && r.Url.Contains("/video_stories")
                && r.Body.Contains("upload_phase=start"));
        }
        finally
        {
            File.Delete(mediaFile);
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task FacebookStory_Video91Seconds_IsBlockedBeforeAnyMetaCall()
    {
        var (db, mediaFile) = await SeedAsync(sizeBytes: 10L * 1024 * 1024);
        var handler = new RecordingMetaHandler();
        // 91s exceeds the 90s Story cap → blocked at preflight.
        var publisher = BuildPublisher(db, mediaFile, handler, durationSeconds: 91, out var postId);

        try
        {
            var result = await publisher.PublishAsync(postId, CancellationToken.None);

            Assert.False(result.Success);
            // Blocked BEFORE any Meta HTTP request (video download + /video_stories both run only
            // after the preflight guard passes).
            Assert.Empty(handler.Requests);
            Assert.Contains("90 seconds", result.ErrorMessage ?? "");
        }
        finally
        {
            File.Delete(mediaFile);
            await db.DisposeAsync();
        }
    }

    [Fact]
    public async Task FacebookStory_VideoOver50MB_IsBlockedBeforeAnyMetaCall()
    {
        // One byte over the 50MB (52,428,800) cap; duration is valid so the ONLY failure is size.
        var (db, mediaFile) = await SeedAsync(sizeBytes: 52_428_801L);
        var handler = new RecordingMetaHandler();
        var publisher = BuildPublisher(db, mediaFile, handler, durationSeconds: 10, out var postId);

        try
        {
            var result = await publisher.PublishAsync(postId, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Empty(handler.Requests); // no Meta call whatsoever
            Assert.Contains("50MB", result.ErrorMessage ?? "");
        }
        finally
        {
            File.Delete(mediaFile);
            await db.DisposeAsync();
        }
    }

    // ── Setup helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an open SQLite-backed context with an owner user, workspace, active Meta
    /// connection + page, and a readable video Media row of <paramref name="sizeBytes"/>.
    /// Returns the context (caller disposes) and the temp media file path (caller deletes).
    /// </summary>
    private static async Task<(AppDbContext db, string mediaFile)> SeedAsync(long sizeBytes)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var owner = new AppUser
        {
            Id = Guid.NewGuid(), Email = "t@example.com", DisplayName = "Tester",
            AuthProvider = "test", ExternalAuthUserId = "ext-1",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.AppUsers.Add(owner);
        db.Workspaces.Add(new Workspace
        {
            Id = Ws, Name = "Test WS", OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, Provider = ProviderType.Meta,
            IsConnected = true, Status = ConnectionStatus.Active,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, MetaConnectionId = conn.Id,
            PageId = "PAGE_FB", Name = "FB Page", AccessToken = "PAGE_TOKEN",
            IsConnected = true, Status = ConnectionStatus.Active,
        };
        db.Add(conn); db.Add(page);

        var storageKey = "users/u/workspaces/w/providers/meta-facebook/media/m/story.mp4";
        var mediaFile = Path.Combine(Path.GetTempPath(), $"fbstorypreflight_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(mediaFile, new byte[] { 0x00 });
        db.Media.Add(new PostPilot.Api.Entities.Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = "story.mp4", ContentType = "video/mp4",
            SizeBytes = sizeBytes, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        });

        db.Posts.Add(new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, Content = string.Empty,
            Platform = Platform.Facebook, PostType = PostType.Story, MediaType = MediaType.Video,
            MediaUrl = storageKey, TargetPageId = page.Id,
            Status = PostStatus.Scheduled, ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return (db, mediaFile);
    }

    private static FacebookStoryPublisher BuildPublisher(
        AppDbContext db, string mediaFile, RecordingMetaHandler handler, double durationSeconds, out Guid postId)
    {
        postId = db.Posts.Single().Id;

        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>())).ReturnsAsync(mediaFile);
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>()));
        mediaService.Setup(m => m.GetPublishingUrlAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://signed.example.com/story.mp4?token=SECRET");

        // Real gate + validation service; only the metadata extractor is faked so the test controls
        // duration while every other check (size, container, readability) runs for real.
        var extractor = new Mock<IVideoMetadataExtractor>();
        extractor.Setup(e => e.ExtractAsync(It.IsAny<string>()))
            .ReturnsAsync(new VideoMetadata(
                Width: 720, Height: 1280, DurationSeconds: durationSeconds,
                Container: "mp4", VideoCodec: "h264", AudioCodec: "aac",
                Fps: 30, Bitrate: null, MimeType: "video/mp4"));

        var gate = new MediaValidationGate(
            db, mediaService.Object,
            new MediaValidationService(
                new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
                extractor.Object,
                NullLogger<MediaValidationService>.Instance),
            NullLogger<MediaValidationGate>.Instance);

        return new FacebookStoryPublisher(
            db,
            Mock.Of<IPostScheduler>(),
            mediaService.Object,
            new HttpClient(handler),
            NullLogger<FacebookStoryPublisher>.Instance,
            Mock.Of<IProviderConnectionService>(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            gate);
    }

    private static PublishingOptions BuildPublishingOptions() => new()
    {
        MediaDownloadUrlExpirationMinutes = 60,
        VideoDownloadUrlExpirationMinutes = 120,
        ImagePollMaxAttempts = 1,
        ImagePollIntervalSeconds = 1,
        OAuthStateExpirationMinutes = 10,
    };

    /// <summary>
    /// Fake Meta transport. Records every request and serves canned success responses for the
    /// three-phase <c>/video_stories</c> upload (start → rupload → finish), the video byte
    /// download, and the permalink fetch — so the publisher runs end-to-end without a real request.
    /// For the "blocked" tests the guard fires first, so nothing is ever recorded here.
    /// </summary>
    private sealed class RecordingMetaHandler : HttpMessageHandler
    {
        public sealed record Captured(HttpMethod Method, string Url, string Body);
        public List<Captured> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Captured(request.Method, url, body));

            string json;
            if (request.Method == HttpMethod.Post && url.Contains("/video_stories") && body.Contains("upload_phase=start"))
                json = """{"video_id":"vid-1","upload_url":"https://rupload.facebook.com/v1/upload-1"}""";
            else if (url.Contains("rupload.facebook.com"))
                json = """{"success":true,"h":"handle-1"}""";
            else if (request.Method == HttpMethod.Post && url.Contains("/video_stories") && body.Contains("upload_phase=finish"))
                json = """{"success":true,"post_id":"story-1"}""";
            else
                json = """{"permalink_url":"https://www.facebook.com/story/1"}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
