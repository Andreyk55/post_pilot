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

public class InstagramPublisherVideoPreflightTests
{
    private static readonly Guid Ws = Guid.Parse("00000000-0000-0000-0000-0000000000f5");

    [Theory]
    [InlineData(PostType.Feed)]
    [InlineData(PostType.Story)]
    public async Task InstagramVideo_OneByteOver50MB_IsBlockedBeforeAnyMetaCall(PostType postType)
    {
        var (db, mediaFile) = await SeedAsync(postType, sizeBytes: 52_428_801L);
        var handler = new RecordingMetaHandler();
        var publisher = BuildPublisher(db, mediaFile, handler, postType, out var postId);

        try
        {
            var result = postType == PostType.Story
                ? await ((InstagramStoryPublisher)publisher).PublishAsync(postId, CancellationToken.None)
                : await ((InstagramPublisher)publisher).PublishAsync(postId, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(PublishErrorType.Permanent, result.ErrorType);
            Assert.Contains("50MB", result.ErrorMessage ?? "");
            Assert.Empty(handler.Requests);
        }
        finally
        {
            File.Delete(mediaFile);
            await db.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(PostType.Feed, "REELS", "video_url")]
    [InlineData(PostType.Story, "STORIES", "video_url")]
    public async Task InstagramVideo_Exactly50MB_PassesPreflight_AndReachesMetaContainerFlow(
        PostType postType,
        string expectedMediaType,
        string expectedUrlField)
    {
        var (db, mediaFile) = await SeedAsync(postType, sizeBytes: 52_428_800L);
        var handler = new RecordingMetaHandler();
        var publisher = BuildPublisher(db, mediaFile, handler, postType, out var postId);

        try
        {
            var result = postType == PostType.Story
                ? await ((InstagramStoryPublisher)publisher).PublishAsync(postId, CancellationToken.None)
                : await ((InstagramPublisher)publisher).PublishAsync(postId, CancellationToken.None);

            Assert.True(result.Success, $"expected publish success; got: {result.ErrorMessage}");
            Assert.Contains(handler.Requests, r =>
                r.Method == HttpMethod.Post
                && r.Url.Contains("/IG_BIZ/media")
                && r.Body.Contains($"media_type={expectedMediaType}")
                && r.Body.Contains($"{expectedUrlField}=https%3A%2F%2Fsigned.example.com%2Fstory.mp4"));
        }
        finally
        {
            File.Delete(mediaFile);
            await db.DisposeAsync();
        }
    }

    private static async Task<(AppDbContext db, string mediaFile)> SeedAsync(PostType postType, long sizeBytes)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var owner = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = "ig-preflight@example.com",
            DisplayName = "Tester",
            AuthProvider = "test",
            ExternalAuthUserId = "ext-ig-preflight",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.AppUsers.Add(owner);
        db.Workspaces.Add(new Workspace
        {
            Id = Ws,
            Name = "IG Preflight",
            OwnerUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            Provider = ProviderType.Meta,
            IsConnected = true,
            Status = ConnectionStatus.Active,
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
            Status = ConnectionStatus.Active,
        };
        var ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            MetaConnectionId = conn.Id,
            PageId = page.PageId,
            PageName = page.Name,
            IgBusinessId = "IG_BIZ",
            Username = "tester",
            IsConnected = true,
            Status = ConnectionStatus.Active,
        };
        db.Add(conn);
        db.Add(page);
        db.Add(ig);

        var storageKey = "users/u/workspaces/w/providers/meta-instagram/media/m/story.mp4";
        var mediaFile = Path.Combine(Path.GetTempPath(), $"igpreflight_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(mediaFile, new byte[] { 0x00 });
        db.Media.Add(new PostPilot.Api.Entities.Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            StorageProvider = "local-disk",
            Bucket = "",
            StorageKey = storageKey,
            OriginalFileName = "story.mp4",
            ContentType = "video/mp4",
            SizeBytes = sizeBytes,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        });

        db.Posts.Add(new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Ws,
            Content = postType == PostType.Story ? string.Empty : "caption",
            Platform = Platform.Instagram,
            PostType = postType,
            MediaType = MediaType.Video,
            MediaUrl = storageKey,
            TargetInstagramAccountId = ig.Id,
            Status = PostStatus.Scheduled,
            ScheduledAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        return (db, mediaFile);
    }

    private static object BuildPublisher(
        AppDbContext db,
        string mediaFile,
        RecordingMetaHandler handler,
        PostType postType,
        out Guid postId)
    {
        postId = db.Posts.Single().Id;

        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(s => s != null && !s.StartsWith("http"));
        mediaService.Setup(m => m.GetLocalFilePathAsync(It.IsAny<string>())).ReturnsAsync(mediaFile);
        mediaService.Setup(m => m.TryCleanupTempLocalPath(It.IsAny<string?>()));
        mediaService.Setup(m => m.GetPublishingUrlAsync(It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://signed.example.com/story.mp4");

        var extractor = new Mock<IVideoMetadataExtractor>();
        extractor.Setup(e => e.ExtractAsync(It.IsAny<string>()))
            .ReturnsAsync(new VideoMetadata(
                Width: postType == PostType.Story ? 720 : 1080,
                Height: postType == PostType.Story ? 1280 : 1080,
                DurationSeconds: 30,
                Container: "mp4",
                VideoCodec: "h264",
                AudioCodec: "aac",
                Fps: 30,
                Bitrate: null,
                MimeType: "video/mp4"));

        var gate = new MediaValidationGate(
            db,
            mediaService.Object,
            new MediaValidationService(
                new ImageMetadataExtractor(NullLogger<ImageMetadataExtractor>.Instance),
                extractor.Object,
                NullLogger<MediaValidationService>.Instance),
            NullLogger<MediaValidationGate>.Instance);

        var publishingOptions = new PublishingOptions
        {
            MediaDownloadUrlExpirationMinutes = 60,
            VideoDownloadUrlExpirationMinutes = 120,
            ImagePollMaxAttempts = 1,
            ImagePollIntervalSeconds = 1,
            OAuthStateExpirationMinutes = 10,
        };

        return postType == PostType.Story
            ? new InstagramStoryPublisher(
                db,
                Mock.Of<IPostScheduler>(),
                mediaService.Object,
                new HttpClient(handler),
                NullLogger<InstagramStoryPublisher>.Instance,
                Mock.Of<IProviderConnectionService>(),
                new MetaApiOptions(),
                publishingOptions,
                gate)
            : new InstagramPublisher(
                db,
                Mock.Of<IPostScheduler>(),
                mediaService.Object,
                new HttpClient(handler),
                NullLogger<InstagramPublisher>.Instance,
                Mock.Of<IProviderConnectionService>(),
                new MetaApiOptions(),
                publishingOptions,
                gate);
    }

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
            if (request.Method == HttpMethod.Post && url.Contains("/IG_BIZ/media_publish"))
                json = """{"id":"media-1"}""";
            else if (request.Method == HttpMethod.Get && url.Contains("/creation-1"))
                json = """{"status_code":"FINISHED","status":"Finished"}""";
            else if (request.Method == HttpMethod.Get && url.Contains("/media-1"))
                json = """{"permalink":"https://instagram.com/p/1","media_type":"VIDEO"}""";
            else
                json = """{"id":"creation-1"}""";

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
