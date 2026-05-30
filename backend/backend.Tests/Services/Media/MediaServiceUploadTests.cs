using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

/// <summary>
/// Tests the application-level guarantees of <see cref="MediaService"/>:
/// content-type allow-list, file-name sanitization, workspace + provider + connection
/// scoped storage keys, and the publish-time fresh signed URL flow.
/// </summary>
public class MediaServiceUploadTests
{
    private const string Unassigned = MediaUploadService.UnassignedProviderSegment;
    private const string NoConn     = MediaUploadService.NoConnectionSegment;

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
                provider: Unassigned,
                providerConnectionId: NoConn,
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
            provider: Unassigned,
            providerConnectionId: NoConn,
            mediaId: Guid.NewGuid(),
            fileName: "ok" + ext,
            contentType: contentType);

        Assert.NotNull(result);
        Assert.StartsWith("workspaces/", result.StorageKey);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_FullKeyShape_WorkspaceProviderConnectionMedia()
    {
        var (svc, _) = Build();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId,
            provider: "meta",
            providerConnectionId: connectionId.ToString("D"),
            mediaId,
            fileName: "photo.png",
            contentType: "image/png");

        // Every required segment, in the briefed order.
        Assert.StartsWith($"workspaces/{workspaceId:D}/", result.StorageKey);
        Assert.Contains($"/providers/meta/", result.StorageKey);
        Assert.Contains($"/connections/{connectionId:D}/", result.StorageKey);
        Assert.Contains($"/media/{mediaId:D}/", result.StorageKey);
        Assert.EndsWith(".png", result.StorageKey);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_FallsBackToUnassignedAndNone_WhenProviderEmpty()
    {
        var (svc, _) = Build();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId,
            provider: "",
            providerConnectionId: "",
            mediaId,
            fileName: "photo.png",
            contentType: "image/png");

        Assert.Contains("/providers/unassigned/", result.StorageKey);
        Assert.Contains("/connections/none/", result.StorageKey);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_SanitizesProviderAndConnectionSegments()
    {
        // Even if some future caller hands us a wild provider/connection string,
        // the resulting key must stay shape-stable. The "/" in "../etc" or the
        // ".." traversal in those segments must NOT introduce extra path levels
        // — the layout MUST stay
        // workspaces/{ws}/providers/{single-segment}/connections/{single-segment}/media/{id}/file
        var (svc, _) = Build();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId,
            provider: "../etc",
            providerConnectionId: "../../secret",
            mediaId,
            fileName: "photo.png",
            contentType: "image/png");

        // ".." must be gone (it was the dangerous part).
        Assert.DoesNotContain("..", result.StorageKey);

        // The KEY SHAPE must remain exactly 7 segments: workspaces, {ws}, providers,
        // {p}, connections, {c}, media, {id}, file → split on '/' gives 9 entries.
        var parts = result.StorageKey.Split('/');
        Assert.Equal(9, parts.Length);
        Assert.Equal("workspaces", parts[0]);
        Assert.Equal(workspaceId.ToString("D"), parts[1]);
        Assert.Equal("providers", parts[2]);
        // Provider segment is a single token — no slashes inside.
        Assert.DoesNotContain("/", parts[3]);
        Assert.Equal("connections", parts[4]);
        Assert.DoesNotContain("/", parts[5]);
        Assert.Equal("media", parts[6]);
        Assert.Equal(mediaId.ToString("D"), parts[7]);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_SanitizesFileName_StripsDirectoryTraversal()
    {
        // The frontend is not trusted with the storage path. Even if it sends
        // "../../../etc/passwd.png", the resulting storage key must stay
        // contained in this workspace/media folder.
        var (svc, _) = Build();
        var workspaceId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();

        var result = await svc.GenerateUploadUrlAsync(
            workspaceId, Unassigned, NoConn, mediaId, "../../../etc/passwd.png", "image/png");

        Assert.StartsWith($"workspaces/{workspaceId:D}/providers/unassigned/connections/none/media/{mediaId:D}/", result.StorageKey);
        Assert.DoesNotContain("..", result.StorageKey);
        Assert.DoesNotContain("/etc/", result.StorageKey);
    }

    [Fact]
    public async Task GenerateUploadUrlAsync_SanitizesFileName_StripsUnicodeAndSpaces()
    {
        var (svc, _) = Build();

        var result = await svc.GenerateUploadUrlAsync(
            Guid.NewGuid(), Unassigned, NoConn, Guid.NewGuid(), "My Cool 📷 Photo.PNG", "image/png");

        // Last segment is the sanitized filename + extension.
        var fileName = result.StorageKey.Split('/').Last();
        Assert.Matches("^[a-z0-9_-]+\\.png$", fileName);
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
            $"workspaces/{Guid.NewGuid():D}/providers/meta/connections/{Guid.NewGuid():D}/media/{Guid.NewGuid():D}/x.png"));

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
