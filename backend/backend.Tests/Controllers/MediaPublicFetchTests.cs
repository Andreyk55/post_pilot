using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Pins the behaviour of the ONE [AllowAnonymous] data-serving route,
/// <c>GET /api/media/files/{*storageKey}</c> (<see cref="MediaController.GetFile"/>).
///
/// <para>
/// Security model (see docs/public-media-route.md): this route is a "capability URL".
/// It is anonymous on purpose — Meta's Facebook/Instagram fetchers pull bytes from it
/// during publishing and present no auth. The safety comes from the storage key being
/// high-entropy (a server-generated GUID mediaId embedded in the path) and from there
/// being NO enumeration endpoint. These tests lock in:
/// </para>
///
/// <list type="bullet">
///   <item>H4 — an anonymous caller holding the EXACT key gets the bytes (Meta needs this).</item>
///   <item>H5 — an anonymous caller with a random/unknown key gets 404, not a hint.</item>
///   <item>H6 — the controller exposes no "list keys" surface; GetFile takes one key and
///         streams only that object, never a directory.</item>
/// </list>
///
/// The route is workspace-blind by design, so these tests don't seed workspaces — they
/// pin that knowledge-of-key is the only gate, which is exactly why the key must carry
/// the entropy. Cross-workspace ownership of the AUTHENTICATED media endpoints is pinned
/// in <see cref="WorkspaceIsolationTests"/>.
/// </summary>
public class MediaPublicFetchTests
{
    private static MediaController NewController(IMediaStorageProvider storage)
    {
        var mediaService = new Mock<IMediaService>();
        mediaService.Setup(m => m.StorageProvider).Returns(storage);
        mediaService.Setup(m => m.RunMode).Returns(AppRunMode.Server);

        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        return new MediaController(
            mediaService.Object,
            new Mock<IMediaUploadService>().Object,
            new Mock<IMediaValidationService>().Object,
            new Mock<ICurrentWorkspaceProvider>().Object,
            db,
            NullLogger<MediaController>.Instance);
    }

    /// <summary>
    /// H4: holding the exact key streams the bytes anonymously. This is the property
    /// that makes Meta publishing work — and the one we accept as the residual risk.
    /// </summary>
    [Fact]
    public async Task GetFile_with_exact_storage_key_streams_bytes()
    {
        var key = $"users/{Guid.NewGuid():D}/workspaces/{Guid.NewGuid():D}/providers/meta-facebook/media/{Guid.NewGuid():D}/photo.jpg";
        var bytes = new byte[] { 1, 2, 3, 4 };
        var storage = new KeyedStorage((key, bytes));

        var result = await NewController(storage).GetFile(key, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
    }

    /// <summary>
    /// H5: a random/unknown key yields 404 — never a 403, a redirect, or any other
    /// response that would confirm "this key exists but you can't have it" and turn
    /// the route into an oracle.
    /// </summary>
    [Fact]
    public async Task GetFile_with_unknown_key_returns_404()
    {
        // Storage knows about ONE real key; the caller guesses a different GUID.
        var realKey = $"media/{Guid.NewGuid():N}.jpg";
        var storage = new KeyedStorage((realKey, new byte[] { 9 }));

        var guessedKey = $"media/{Guid.NewGuid():N}.jpg";
        var result = await NewController(storage).GetFile(guessedKey, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// H5 (boundary): an empty/whitespace key is rejected outright — the catch-all
    /// route must not be coaxed into serving a directory root.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetFile_with_blank_key_returns_404(string key)
    {
        var storage = new KeyedStorage();

        var result = await NewController(storage).GetFile(key, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>
    /// H6: the public route serves exactly the one object named by the key and nothing
    /// else. There is no list/enumerate path: GetFile asks the provider for a single
    /// key via OpenReadAsync, so a caller can never coax a directory listing out of it.
    /// </summary>
    [Fact]
    public async Task GetFile_requests_only_the_named_key_and_never_enumerates()
    {
        var key = $"media/{Guid.NewGuid():N}.png";
        var storage = new KeyedStorage((key, new byte[] { 7, 7 }));

        await NewController(storage).GetFile(key, CancellationToken.None);

        // Exactly one read, for exactly the key the caller named.
        Assert.Equal(new[] { key }, storage.OpenedKeys);
    }

    /// <summary>
    /// Minimal storage double keyed by exact storage key. Unknown keys return null
    /// (the contract <see cref="MediaController.GetFile"/> turns into a 404). Records
    /// every key it was asked to open so a test can prove no extra/enumerating reads.
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
