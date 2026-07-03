using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Pins the media-privacy redesign: media file access is now ONLY through the authenticated,
/// workspace-scoped <c>GET /api/media/{mediaId}/file</c> (<see cref="MediaController.GetMediaFile"/>).
/// The former anonymous catch-all route <c>GET /api/media/files/{*storageKey}</c>
/// (<c>MediaController.GetFile</c>) has been removed entirely — it is no longer reachable by
/// any HTTP method, anonymous or authenticated. See docs/public-media-route.md for the
/// (now-historical) rationale and the migration.
/// </summary>
public class MediaPublicFetchTests
{
    /// <summary>
    /// The old anonymous action no longer exists on the controller at all — this is stronger
    /// than "returns 404" (a 404 could also mean "route exists but not found"; here there is no
    /// action, so ASP.NET routing itself can never match a request to it).
    /// </summary>
    [Fact]
    public void GetFile_action_no_longer_exists_on_MediaController()
    {
        var method = typeof(MediaController).GetMethod("GetFile", BindingFlags.Public | BindingFlags.Instance);
        Assert.Null(method);
    }

    /// <summary>
    /// The two obsolete upload endpoints (POST /api/media/upload-url and
    /// PUT /api/media/upload/{filename}) are removed entirely, not merely disabled.
    /// </summary>
    [Fact]
    public void GenerateUploadUrl_action_no_longer_exists_on_MediaController()
    {
        var method = typeof(MediaController).GetMethod("GenerateUploadUrl", BindingFlags.Public | BindingFlags.Instance);
        Assert.Null(method);
    }

    [Fact]
    public void UploadFile_action_no_longer_exists_on_MediaController()
    {
        var method = typeof(MediaController).GetMethod("UploadFile", BindingFlags.Public | BindingFlags.Instance);
        Assert.Null(method);
    }

    /// <summary>
    /// The new mediaId-based file endpoint carries NO [AllowAnonymous] — it inherits the
    /// controller-level [Authorize], so an unauthenticated caller is rejected by the auth
    /// middleware before the action ever runs (pinned at the controller-plumbing layer since
    /// this test project has no WebApplicationFactory to exercise the full HTTP pipeline).
    /// </summary>
    [Fact]
    public void GetMediaFile_has_no_AllowAnonymous_attribute()
    {
        var method = typeof(MediaController).GetMethod(nameof(MediaController.GetMediaFile));
        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<AllowAnonymousAttribute>());

        var controllerAuthorize = typeof(MediaController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(controllerAuthorize);
    }

    /// <summary>
    /// GetFrame (local-dev-only extracted video frames) is intentionally out of scope for this
    /// redesign and remains anonymous — pin that it still exists so a future refactor doesn't
    /// accidentally remove/rename it without updating this expectation.
    /// </summary>
    [Fact]
    public void GetFrame_is_still_anonymous_and_unaffected()
    {
        var method = typeof(MediaController).GetMethod(nameof(MediaController.GetFrame));
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public async Task GetMediaFile_with_own_workspace_mediaId_streams_bytes()
    {
        var workspaceId = Guid.NewGuid();
        var key = $"users/{Guid.NewGuid():D}/workspaces/{workspaceId:D}/providers/meta-facebook/media/{Guid.NewGuid():D}/photo.jpg";
        var (controller, db) = NewController(new KeyedStorage((key, new byte[] { 1, 2, 3, 4 })), workspaceId);
        var media = SeedMedia(db, workspaceId, key, "image/jpeg");

        var result = await controller.GetMediaFile(media.Id, null, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    /// <summary>
    /// A random/unknown mediaId yields 404 — never a 403, a redirect, or any other response
    /// that would confirm "this exists but you can't have it".
    /// </summary>
    [Fact]
    public async Task GetMediaFile_with_unknown_mediaId_returns_404()
    {
        var (controller, _) = NewController(new KeyedStorage(), Guid.NewGuid());

        var result = await controller.GetMediaFile(Guid.NewGuid(), null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// A mediaId that exists but belongs to a DIFFERENT workspace than the caller's current one
    /// returns the exact same 404 as an unknown mediaId — the response never discloses that the
    /// row exists in someone else's workspace.
    /// </summary>
    [Fact]
    public async Task GetMediaFile_with_foreign_workspace_mediaId_returns_404_not_403()
    {
        var callerWorkspaceId = Guid.NewGuid();
        var foreignWorkspaceId = Guid.NewGuid();
        const string key = "media/foreign-owner.jpg";
        var (controller, db) = NewController(new KeyedStorage((key, new byte[] { 9 })), callerWorkspaceId);
        var media = SeedMedia(db, foreignWorkspaceId, key, "image/jpeg");

        var result = await controller.GetMediaFile(media.Id, null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// <c>?variant=thumbnail</c> serves the derived thumbnail object, not the original asset.
    /// </summary>
    [Fact]
    public async Task GetMediaFile_thumbnail_variant_serves_thumbnail_storage_key()
    {
        var workspaceId = Guid.NewGuid();
        const string originalKey = "media/original.png";
        const string thumbnailKey = "media/original-thumb.jpg";
        var storage = new KeyedStorage((originalKey, new byte[] { 1 }), (thumbnailKey, new byte[] { 2 }));
        var (controller, db) = NewController(storage, workspaceId);
        var media = SeedMedia(db, workspaceId, originalKey, "image/png");
        media.ThumbnailStorageKey = thumbnailKey;
        media.ThumbnailMimeType = "image/jpeg";
        db.SaveChanges();

        var result = await controller.GetMediaFile(media.Id, "thumbnail", CancellationToken.None);

        Assert.IsType<FileStreamResult>(result);
        Assert.Equal(new[] { thumbnailKey }, storage.OpenedKeys);
    }

    /// <summary>
    /// A mediaId with no thumbnail derivative returns 404 for the thumbnail variant even though
    /// the original asset exists — it must not silently fall back to serving the original.
    /// </summary>
    [Fact]
    public async Task GetMediaFile_thumbnail_variant_without_derivative_returns_404()
    {
        var workspaceId = Guid.NewGuid();
        const string originalKey = "media/no-thumb.png";
        var (controller, db) = NewController(new KeyedStorage((originalKey, new byte[] { 1 })), workspaceId);
        var media = SeedMedia(db, workspaceId, originalKey, "image/png");

        var result = await controller.GetMediaFile(media.Id, "thumbnail", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    private static (MediaController Controller, AppDbContext Db) NewController(IMediaStorageProvider storage, Guid workspaceId)
    {
        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.StorageProvider).Returns(storage);
        mediaService.Setup(m => m.RunMode).Returns(AppRunMode.Server);

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var workspaceProvider = new Mock<ICurrentWorkspaceProvider>();
        workspaceProvider.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceId);

        var controller = new MediaController(
            mediaService.Object,
            new Mock<IMediaUploadService>().Object,
            new Mock<IMediaValidationService>().Object,
            new Mock<IMediaValidationGate>().Object,
            workspaceProvider.Object,
            db,
            NullLogger<MediaController>.Instance)
        {
            // GetMediaFile writes response headers (nosniff / content-disposition); give it a context.
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return (controller, db);
    }

    private static Media SeedMedia(AppDbContext db, Guid workspaceId, string storageKey, string contentType)
    {
        var media = new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StorageProvider = "local-disk",
            StorageKey = storageKey,
            ContentType = contentType,
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        };
        db.Media.Add(media);
        db.SaveChanges();
        return media;
    }

    /// <summary>
    /// Minimal storage double keyed by exact storage key. Unknown keys return null.
    /// Records every key it was asked to open so a test can prove no extra/enumerating reads.
    /// </summary>
    private sealed class KeyedStorage : IMediaStorageProvider
    {
        private readonly Dictionary<string, byte[]> _objects;
        public List<string> OpenedKeys { get; } = new();

        public KeyedStorage(params (string key, byte[] bytes)[] objects)
            => _objects = objects.ToDictionary(o => o.key, o => o.bytes);

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            OpenedKeys.Add(storageKey);
            if (_objects.TryGetValue(storageKey, out var bytes))
                return Task.FromResult<Stream?>(new MemoryStream(bytes));
            return Task.FromResult<Stream?>(null);
        }

        public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://upload.test/" + storageKey);
        public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://download.test/" + storageKey);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UploadObjectAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool Exists(string storageKey) => _objects.ContainsKey(storageKey);
        public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(_objects.ContainsKey(storageKey));
        public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<StoredObjectInfo?>(null);
    }
}
