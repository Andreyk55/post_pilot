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
/// Proves the Facebook Story publisher no longer blocks a readable MP4/MOV on its video or audio
/// codec: an uncommon codec (previously rejected) passes the shared pre-Meta preflight gate and
/// the publisher proceeds to the Meta <c>/video_stories</c> flow, where Meta — not Publish Harbor —
/// decides whether the codec is playable.
///
/// <para>This drives the FULL <see cref="FacebookStoryPublisher.PublishAsync"/> path (unlike the
/// preflight-only <c>StoryPublisherContentGuardTests</c>) so the assertion is that a Meta HTTP
/// request is actually attempted. The full path uses <c>ExecuteUpdateAsync</c> (claim), which the
/// EF InMemory provider does not support, so this test runs over SQLite. A recording fake handler
/// stands in for Meta — no real request is made. Codec rejection for OTHER placements is proven in
/// <c>MediaValidationGateTests.Video_Codec_RejectedForFeedAndInstagram_ButAcceptedForFacebookStory</c>.</para>
/// </summary>
public class FacebookStoryVideoCodecPublisherTests
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    [Theory]
    [InlineData("prores", "aac")]      // video codec previously rejected for FB Story
    [InlineData("vp9", "opus")]        // both previously rejected
    [InlineData("av1", "mp3")]         // both previously rejected
    [InlineData("h264", null)]         // no audio stream
    [InlineData(null, null)]           // unknown/missing codec names (extraction still succeeded)
    public async Task FacebookStory_UncommonCodec_PassesPreflight_AndReachesMetaVideoStoryFlow(
        string? videoCodec, string? audioCodec)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Owner user + parent workspace (SQLite enforces the AppUser/Workspace FKs that the EF
        // InMemory provider skips).
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

        // Target page + connection: owned and active so the publish gate does not block.
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

        // A readable video Media row within the size cap. Its bytes are irrelevant — the video
        // metadata (codec/duration/dims) comes from the faked extractor below.
        var storageKey = "users/u/workspaces/w/providers/meta-facebook/media/m/story.mp4";
        var mediaFile = Path.Combine(Path.GetTempPath(), $"fbstorycodec_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(mediaFile, new byte[] { 0x00 });
        db.Media.Add(new PostPilot.Api.Entities.Media
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, StorageProvider = "local-disk", Bucket = "",
            StorageKey = storageKey, OriginalFileName = "story.mp4", ContentType = "video/mp4",
            SizeBytes = 10L * 1024 * 1024, Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow, UploadedAt = DateTime.UtcNow,
        });

        var post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = Ws, Content = string.Empty,
            Platform = Platform.Facebook, PostType = PostType.Story, MediaType = MediaType.Video,
            MediaUrl = storageKey, TargetPageId = page.Id,
            Status = PostStatus.Scheduled, ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Posts.Add(post);
        await db.SaveChangesAsync();

        // Media service: resolves the storage key to the seeded temp file and hands the publisher a
        // fake signed download URL (the recording handler serves the byte download).
        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>())).ReturnsAsync(mediaFile);
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>()));
        mediaService.Setup(m => m.GetPublishingUrlAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://signed.example.com/story.mp4?token=SECRET");

        // Real gate + real validation service; only the metadata extractor is faked so the test
        // controls the (uncommon) codec while every other check runs for real.
        var extractor = new Mock<IVideoMetadataExtractor>();
        extractor.Setup(e => e.ExtractAsync(It.IsAny<string>()))
            .ReturnsAsync(new VideoMetadata(
                Width: 720, Height: 1280, DurationSeconds: 10,
                Container: "mp4",
                VideoCodec: videoCodec,
                AudioCodec: audioCodec,
                Fps: 30, Bitrate: null, MimeType: "video/mp4"));

        var gate = new MediaValidationGate(
            db, mediaService.Object,
            new MediaValidationService(
                new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
                extractor.Object,
                NullLogger<MediaValidationService>.Instance),
            NullLogger<MediaValidationGate>.Instance);

        var handler = new RecordingMetaHandler();
        var publisher = new FacebookStoryPublisher(
            db,
            Mock.Of<IPostScheduler>(),
            mediaService.Object,
            new HttpClient(handler),
            NullLogger<FacebookStoryPublisher>.Instance,
            Mock.Of<IProviderConnectionService>(),
            new MetaApiOptions(),
            BuildPublishingOptions(),
            gate);

        try
        {
            var result = await publisher.PublishAsync(post.Id, CancellationToken.None);

            // Preflight passed for the uncommon codec: the publisher reached the Meta video-story
            // START request (i.e. NO codec rejection occurred before the HTTP call) and the flow
            // completed successfully against the fake handler.
            Assert.True(result.Success, $"expected publish success; got: {result.ErrorMessage}");
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Post
                && r.Url.Contains("/video_stories")
                && r.Body.Contains("upload_phase=start"));
            // And no codec-related failure surfaced anywhere in the result.
            Assert.DoesNotContain("codec", result.ErrorMessage ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(mediaFile);
        }
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
    /// Fake Meta transport. Records every request and returns canned success responses for the
    /// three-phase <c>/video_stories</c> upload (start → rupload → finish), the video byte
    /// download, and the permalink fetch — so the publisher runs end-to-end without a real request.
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
                // Video byte download (GET signed URL) and permalink fetch both land here.
                json = """{"permalink_url":"https://www.facebook.com/story/1"}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
