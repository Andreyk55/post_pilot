using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

/// <summary>
/// Tenant-isolation tests for <see cref="MediaUploadService"/>. The whole upload/
/// complete/delete API trusts the workspaceId argument; it MUST come from the
/// authenticated session (the controller pulls it from
/// <c>ICurrentWorkspaceProvider</c>). These tests verify that handing the
/// service a workspaceId for workspace A cannot read or delete media that
/// belongs to workspace B, and that a Meta connection id from workspace B
/// cannot be used to scope an upload under workspace A's prefix.
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
            StorageKey = $"workspaces/{workspaceA:D}/providers/unassigned/connections/none/media/{mediaId:D}/photo.png",
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
                workspaceId: Guid.NewGuid(),
                fileName: "huge.png",
                contentType: "image/png",
                sizeBytes: 2048));
    }

    [Fact]
    public async Task InitAsync_RejectsUnsupportedContentType()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.InitAsync(
                workspaceId: Guid.NewGuid(),
                fileName: "doc.pdf",
                contentType: "application/pdf",
                sizeBytes: 100));
    }

    [Fact]
    public async Task InitAsync_BackendChoosesPath_FrontendNameIsSanitized()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);
        var workspaceId = Guid.NewGuid();

        var result = await svc.InitAsync(
            workspaceId, "../../../etc/passwd.png", "image/png", 100);

        // Even though the frontend supplied a traversal-style name, the storage
        // key must stay inside this workspace's media folder, under the reserved
        // unassigned/none segments since no provider was supplied.
        Assert.StartsWith(
            $"workspaces/{workspaceId:D}/providers/unassigned/connections/none/media/{result.MediaId:D}/",
            result.StorageKey);
        Assert.DoesNotContain("..", result.StorageKey);
        Assert.DoesNotContain("/etc/", result.StorageKey);

        // The Media row was created with the SAME mediaId we returned and
        // the workspace the caller passed in.
        var row = await db.Media.FirstAsync(m => m.Id == result.MediaId);
        Assert.Equal(workspaceId, row.WorkspaceId);
        Assert.Equal(MediaUploadStatus.PendingUpload, row.Status);
    }

    [Fact]
    public async Task InitAsync_WithOwnMetaConnection_EmbedsConnectionIdInKey()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);

        var workspaceId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        db.Set<MetaConnection>().Add(new MetaConnection
        {
            Id = connectionId,
            WorkspaceId = workspaceId,
            UserId = Guid.NewGuid(),
            AccessToken = "tok",
            TokenExpiresAt = DateTime.UtcNow.AddHours(1),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Provider = ProviderType.Meta,
        });
        await db.SaveChangesAsync();

        var result = await svc.InitAsync(
            workspaceId,
            "photo.png",
            "image/png",
            100,
            provider: ProviderType.Meta,
            providerConnectionId: connectionId);

        Assert.Contains($"/providers/meta/", result.StorageKey);
        Assert.Contains($"/connections/{connectionId:D}/", result.StorageKey);
    }

    [Fact]
    public async Task InitAsync_WithConnectionFromAnotherWorkspace_IsRejected()
    {
        // The whole point of the connection-scoped path: passing another
        // workspace's connection id must NOT let me sneak an upload under
        // that workspace's prefix. We collapse "not found" and "wrong
        // workspace" into the same UnauthorizedAccessException so callers
        // can't distinguish the two via timing or error text.
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);

        var attackerWorkspace = Guid.NewGuid();
        var victimWorkspace = Guid.NewGuid();
        var victimConnectionId = Guid.NewGuid();
        db.Set<MetaConnection>().Add(new MetaConnection
        {
            Id = victimConnectionId,
            WorkspaceId = victimWorkspace,
            UserId = Guid.NewGuid(),
            AccessToken = "tok",
            TokenExpiresAt = DateTime.UtcNow.AddHours(1),
            ConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Provider = ProviderType.Meta,
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.InitAsync(
                attackerWorkspace,
                "photo.png",
                "image/png",
                100,
                provider: ProviderType.Meta,
                providerConnectionId: victimConnectionId));

        // No Media row should have been created on the failure path.
        Assert.False(await db.Media.AnyAsync());
    }

    [Fact]
    public async Task InitAsync_WithUnknownConnectionId_IsRejected()
    {
        var db = NewDb();
        var media = NewMediaService(new RecordingStorage());
        var svc = new MediaUploadService(db, media, Opts(), NullLogger<MediaUploadService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.InitAsync(
                Guid.NewGuid(),
                "photo.png",
                "image/png",
                100,
                provider: ProviderType.Meta,
                providerConnectionId: Guid.NewGuid()));
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
