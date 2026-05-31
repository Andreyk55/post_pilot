using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

/// <summary>
/// Tenant-isolation tests for <see cref="MediaUploadService"/>. The upload/complete/
/// delete API trusts the workspaceId argument; it MUST come from the authenticated
/// session (the controller pulls it from <c>ICurrentWorkspaceProvider</c>). These
/// tests verify that handing the service a workspaceId for workspace A cannot read
/// or delete media that belongs to workspace B, and that the storage key generation
/// is platform-partitioned per the MVP rule.
/// </summary>
public class MediaUploadServiceAuthorizationTests
{
    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static MediaStorageOptions Opts() => new()
    {
        Provider = "supabase",
        Supabase = new SupabaseStorageOptions
        {
            Url = "https://abc.supabase.co",
            ServiceRoleKey = "k",
            Bucket = "postpilot-media",
            SignedUrlExpirySeconds = 3600,
            MaxUploadBytes = 0,
        },
    };

    private static MediaService NewMediaService(IMediaStorageProvider storage) => new(
        storage: storage,
        storageOpts: Opts(),
        runMode: AppRunMode.Server,
        logger: NullLogger<MediaService>.Instance,
        uploadUrlExpiration: TimeSpan.FromMinutes(15),
        maxImageFileSizeBytes: 20 * 1024 * 1024,
        maxVideoFileSizeBytes: 200 * 1024 * 1024,
        publishingBaseUrl: "https://post-pilot.cloud-ip.cc",
        defaultPublishingUrlExpiration: TimeSpan.FromHours(1));

    [Fact]
    public async Task DeleteAsync_FromWrongWorkspace_ReturnsFalse_AndLeavesStorageUntouched()
    {
        var db = NewDb();
        var storage = new RecordingStorage();
        var media = NewMediaService(storage);
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);

        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();

        // Workspace A owns a media row.
        var mediaId = Guid.NewGuid();
        db.Media.Add(new Entities.Media
        {
            Id = mediaId,
            WorkspaceId = workspaceA,
            StorageProvider = "supabase",
            Bucket = "postpilot-media",
            StorageKey = $"users/{Guid.NewGuid():D}/workspaces/{workspaceA:D}/providers/meta-facebook/media/{mediaId:D}/photo.png",
            OriginalFileName = "photo.png",
            ContentType = "image/png",
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        // Workspace B tries to delete it.
        var removed = await svc.DeleteAsync(workspaceB, mediaId, CancellationToken.None);

        Assert.False(removed);
        // Storage must NOT have been touched — that's the key invariant.
        Assert.Empty(storage.DeletedKeys);
        // Row is still there, status unchanged.
        var row = await db.Media.FirstAsync(m => m.Id == mediaId);
        Assert.Equal(MediaUploadStatus.Uploaded, row.Status);
    }

    [Fact]
    public async Task InitAsync_RejectsOversizedFile()
    {
        var db = NewDb();
        var storage = new RecordingStorage();
        var media = NewMediaService(storage);
        var opts = Opts();
        opts.Supabase.MaxUploadBytes = 1024; // tiny cap for this test
        var svc = new MediaUploadService(db, media, opts, NullLogger<MediaUploadService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.InitAsync(
                userId: Guid.NewGuid(),
                workspaceId: Guid.NewGuid(),
                fileName: "huge.png",
                contentType: "image/png",
                sizeBytes: 2048,
                platform: Platform.Facebook));
    }

    [Fact]
    public async Task InitAsync_RejectsUnsupportedContentType()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.InitAsync(
                userId: Guid.NewGuid(),
                workspaceId: Guid.NewGuid(),
                fileName: "doc.pdf",
                contentType: "application/pdf",
                sizeBytes: 100,
                platform: Platform.Instagram));
    }

    [Fact]
    public async Task InitAsync_RejectsUnsupportedPlatform()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.InitAsync(
                userId: Guid.NewGuid(),
                workspaceId: Guid.NewGuid(),
                fileName: "photo.png",
                contentType: "image/png",
                sizeBytes: 100,
                platform: Platform.LinkedIn));

        // No Media row should have been created on the validation-failure path.
        Assert.False(await db.Media.AnyAsync());
    }

    [Fact]
    public async Task InitAsync_Facebook_BackendChoosesPath_FrontendNameIsSanitized()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        // Frontend hands us a traversal-style name AND tries to push a different
        // "platform" by spelling. The backend ignores anything except the typed
        // Platform enum value and the sanitized file name.
        var result = await svc.InitAsync(
            userId, workspaceId, "../../../etc/passwd.png", "image/png", 100, Platform.Facebook);

        Assert.StartsWith(
            $"users/{userId:D}/workspaces/{workspaceId:D}/providers/meta-facebook/media/{result.MediaId:D}/",
            result.StorageKey);
        Assert.DoesNotContain("..", result.StorageKey);
        Assert.DoesNotContain("/etc/", result.StorageKey);
        Assert.DoesNotContain("/connections/", result.StorageKey);
        Assert.DoesNotContain("/providers/unassigned/", result.StorageKey);

        // The Media row was created with the SAME mediaId we returned and
        // the workspace the caller passed in.
        var row = await db.Media.FirstAsync(m => m.Id == result.MediaId);
        Assert.Equal(workspaceId, row.WorkspaceId);
        Assert.Equal(MediaUploadStatus.PendingUpload, row.Status);
        Assert.Equal("supabase", row.StorageProvider);
        Assert.Equal("postpilot-media", row.Bucket);
    }

    [Fact]
    public async Task InitAsync_Instagram_UsesMetaInstagramSegment()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        var result = await svc.InitAsync(
            userId, workspaceId, "reel.mp4", "video/mp4", 100, Platform.Instagram);

        Assert.StartsWith(
            $"users/{userId:D}/workspaces/{workspaceId:D}/providers/meta-instagram/media/{result.MediaId:D}/",
            result.StorageKey);
        Assert.EndsWith(".mp4", result.StorageKey);
    }

    private sealed class RecordingStorage : IMediaStorageProvider
    {
        public List<string> DeletedKeys { get; } = new();
        public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://example/upload/" + storageKey);
        public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://example/download/" + storageKey);
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(storageKey);
            return Task.CompletedTask;
        }
        public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool Exists(string storageKey) => false;
        public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<StoredObjectInfo?>(null);
    }
}
