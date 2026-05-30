using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

/// <summary>
/// Tests the application-level guarantees of <see cref="MediaService"/>:
/// content-type allow-list, file-name sanitization, workspace + platform scoped
/// storage keys, and the publish-time fresh signed URL flow.
/// </summary>
public class MediaServiceUploadTests
{
    private static (MediaService svc, FakeStorageProvider fake) Build(string provider = "supabase")
    {
        var fake = new FakeStorageProvider();
        var opts = new MediaStorageOptions
        {
            Provider = provider,
            Supabase = new SupabaseStorageOptions
            {
                Url = "https://abc.supabase.co",
                ServiceRoleKey = "k",
                Bucket = "postpilot-media",
                SignedUrlExpirySeconds = 3600,
            },
        };
        var svc = new MediaService(
            storage: fake,
            storageOpts: opts,
            runMode: AppRunMode.Server,
            logger: NullLogger<MediaService>.Instance,
            uploadUrlExpiration: TimeSpan.FromMinutes(15),
            maxImageFileSizeBytes: 20 * 1024 * 1024,
            maxVideoFileSizeBytes: 200 * 1024 * 1024,
            publishingBaseUrl: "https://post-pilot.cloud-ip.cc",
            defaultPublishingUrlExpiration: TimeSpan.FromHours(1));
        return (svc, fake);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_RejectsUnsupportedContentType()
    {
        var (svc, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GenerateUploadUrlAsync(
                workspaceId: Guid.NewGuid(),
                platform: Platform.Facebook,
                mediaId: Guid.NewGuid(),
                fileName: "evil.exe",
                contentType: "application/x-msdownload"));
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("image/gif")]
    [InlineData("video/mp4")]
    [InlineData("video/quicktime")]
    public async Task GenerateUploadUrlAsync_AcceptsBriefedTypes(string contentType)
    {
        var (svc, _) = Build();

        var ext = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            _ => throw new InvalidOperationException(),
        };

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId: Guid.NewGuid(),
            platform: Platform.Instagram,
            mediaId: Guid.NewGuid(),
            fileName: "ok" + ext,
            contentType: contentType);

        Assert.NotNull(result);
        Assert.StartsWith("workspaces/", result.StorageKey);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_Facebook_UsesMetaFacebookSegment()
    {
        var (svc, _) = Build();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId, Platform.Facebook, mediaId, "photo.png", "image/png");

        // Final shape: workspaces/{ws}/providers/meta-facebook/media/{mediaId}/{safe}.png
        Assert.StartsWith($"workspaces/{workspaceId:D}/providers/meta-facebook/media/{mediaId:D}/", result.StorageKey);
        Assert.EndsWith(".png", result.StorageKey);
        // Old shape from the previous iteration must NOT survive.
        Assert.DoesNotContain("/connections/", result.StorageKey);
        Assert.DoesNotContain("/providers/unassigned/", result.StorageKey);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_Instagram_UsesMetaInstagramSegment()
    {
        var (svc, _) = Build();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId, Platform.Instagram, mediaId, "video.mp4", "video/mp4");

        Assert.StartsWith($"workspaces/{workspaceId:D}/providers/meta-instagram/media/{mediaId:D}/", result.StorageKey);
        Assert.EndsWith(".mp4", result.StorageKey);
        Assert.DoesNotContain("/connections/", result.StorageKey);
        Assert.DoesNotContain("/providers/unassigned/", result.StorageKey);
    }

    [Theory]
    [InlineData(Platform.Twitter)]
    [InlineData(Platform.LinkedIn)]
    public async Task GenerateUploadUrlAsync_UnsupportedPlatform_IsRejected(Platform platform)
    {
        // MVP: only Facebook + Instagram have a defined storage segment. New
        // platforms must be added explicitly to MediaService.MapPlatformToProviderSegment.
        var (svc, _) = Build();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.GenerateUploadUrlAsync(
                Guid.NewGuid(), platform, Guid.NewGuid(), "photo.png", "image/png"));
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_SanitizesFileName_StripsDirectoryTraversal()
    {
        // The frontend is not trusted with the storage path. Even if it sends
        // "../../../etc/passwd.png", the resulting storage key must stay
        // contained in this workspace/platform/media folder.
        var (svc, _) = Build();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId, Platform.Facebook, mediaId, "../../../etc/passwd.png", "image/png");

        Assert.StartsWith(
            $"workspaces/{workspaceId:D}/providers/meta-facebook/media/{mediaId:D}/",
            result.StorageKey);
        Assert.DoesNotContain("..", result.StorageKey);
        Assert.DoesNotContain("/etc/", result.StorageKey);

        // Exactly 7 segments: workspaces, {ws}, providers, meta-facebook, media, {id}, file.
        // Splitting on '/' gives 7 parts (no leading slash on storage keys).
        var parts = result.StorageKey.Split('/');
        Assert.Equal(7, parts.Length);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_SanitizesFileName_StripsUnicodeAndSpaces()
    {
        var (svc, _) = Build();

        var result = await svc.GenerateUploadUrlAsync(
            Guid.NewGuid(), Platform.Instagram, Guid.NewGuid(), "My Cool 📷 Photo.PNG", "image/png");

        // Last segment is the sanitized filename + extension.
        var fileName = result.StorageKey.Split('/').Last();
        Assert.Matches("^[a-z0-9_-]+\\.png$", fileName);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_PreservesExtensionFromContentType_WhenFileNameHasNone()
    {
        var (svc, _) = Build();

        var result = await svc.GenerateUploadUrlAsync(
            Guid.NewGuid(), Platform.Facebook, Guid.NewGuid(), "photo-no-ext", "image/png");

        Assert.EndsWith(".png", result.StorageKey);
    }

    [Fact]
    public async Task GetPublishingUrlAsync_OnSupabase_CallsCreateDownloadUrl()
    {
        // The whole point of the Supabase migration: at publish time the worker
        // hands Meta a SIGNED URL pointing at supabase.co, not a stable
        // /api/media/files/... proxy. This is what makes 30-day-future scheduled
        // posts work — they ask for a fresh signed URL when publishing fires.
        var (svc, fake) = Build("supabase");
        fake.NextDownloadUrl = "https://abc.supabase.co/storage/v1/object/sign/postpilot-media/key?token=fresh";

        var url = await svc.GetPublishingUrlAsync("media/foo.png");

        Assert.Equal(1, fake.CreateDownloadUrlCalls);
        Assert.Equal("https://abc.supabase.co/storage/v1/object/sign/postpilot-media/key?token=fresh", url);
    }

    [Fact]
    public async Task GetPublishingUrlAsync_TwoCalls_TwoFreshSignatures()
    {
        // Worker generates a fresh signed URL at publish time. Calling twice
        // must hit the provider twice so a scheduled-far-in-the-future post
        // does not depend on a URL that was minted at scheduling time.
        var (svc, fake) = Build("supabase");

        await svc.GetPublishingUrlAsync("media/foo.png");
        await svc.GetPublishingUrlAsync("media/foo.png");

        Assert.Equal(2, fake.CreateDownloadUrlCalls);
    }

    [Fact]
    public async Task GetPublishingUrlAsync_OnLocalDisk_FallsBackToApiProxy()
    {
        // local-disk has no bucket to sign against — fall back to the
        // /api/media/files/... proxy rooted at App.PublicUrl.
        var (svc, fake) = Build("local-disk");

        var url = await svc.GetPublishingUrlAsync("media/foo.png");

        Assert.Equal(0, fake.CreateDownloadUrlCalls);
        Assert.Equal("https://post-pilot.cloud-ip.cc/api/media/files/media/foo.png", url);
    }

    [Fact]
    public void IsStorageKey_AcceptsBothLegacyAndWorkspaceScoped()
    {
        var (svc, _) = Build();

        Assert.True(svc.IsStorageKey("media/abc.png"));
        Assert.True(svc.IsStorageKey(
            $"workspaces/{Guid.NewGuid():D}/providers/meta-facebook/media/{Guid.NewGuid():D}/x.png"));

        // External URLs and null/empty must NOT be storage keys (they go
        // straight to Meta as-is).
        Assert.False(svc.IsStorageKey("https://example.com/foo.jpg"));
        Assert.False(svc.IsStorageKey(""));
        Assert.False(svc.IsStorageKey(null));
    }

    // ── Test double ──────────────────────────────────────────────────────────
    private sealed class FakeStorageProvider : IMediaStorageProvider
    {
        public int CreateDownloadUrlCalls;
        public string NextDownloadUrl { get; set; } = "https://example/signed";

        public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://example/upload/" + storageKey);

        public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
        {
            CreateDownloadUrlCalls++;
            return Task.FromResult(NextDownloadUrl);
        }

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
        public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public bool Exists(string storageKey) => false;
        public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredObjectInfo?>(null);
    }
}
