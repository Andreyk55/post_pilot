using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

public class MediaUploadServiceInstagramDerivativeTests : IDisposable
{
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName)
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

    private MediaUploadService NewUploadService(AppDbContext db, RecordingStorage storage) => new(
        db,
        NewMediaService(storage),
        Opts(),
        NullLogger<MediaUploadService>.Instance,
        new InstagramDerivativeService(NullLogger<InstagramDerivativeService>.Instance));

    [Fact]
    public async Task CompleteAsync_PngUpload_StoresInstagramJpegDerivative()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var svc = NewUploadService(db, storage);
        var workspaceId = Guid.NewGuid();

        var init = await svc.InitAsync(Guid.NewGuid(), workspaceId, "photo.png", "image/png", 1000, Platform.Instagram);
        storage.SeedLocalFile(init.StorageKey, WriteImage("png", 1080, 1080), "image/png");

        await svc.CompleteAsync(workspaceId, init.MediaId);

        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Equal(init.StorageKey, row.StorageKey);
        Assert.Equal("image/jpeg", row.InstagramImageMimeType);
        Assert.Equal($"{FolderOf(init.StorageKey)}/photo.jpg", row.InstagramImageStorageKey);
        Assert.False(row.InstagramImageStorageKey!.EndsWith("/instagram.jpg", StringComparison.Ordinal));
        Assert.Equal(1080, row.InstagramImageWidth);
        Assert.Equal(1080, row.InstagramImageHeight);
        Assert.True(row.InstagramImageSizeBytes > 0);
        Assert.NotNull(row.InstagramImageGeneratedAt);

        Assert.True(storage.UploadedObjects.ContainsKey(row.InstagramImageStorageKey!));
        await using var derivativeBytes = new MemoryStream(storage.UploadedObjects[row.InstagramImageStorageKey!]);
        using var image = await Image.LoadAsync(derivativeBytes);
        Assert.Equal("JPEG", image.Metadata.DecodedImageFormat?.Name);
    }

    [Fact]
    public async Task CompleteAsync_FacebookPngUpload_DoesNotCreateDerivative()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var svc = NewUploadService(db, storage);
        var workspaceId = Guid.NewGuid();

        var init = await svc.InitAsync(Guid.NewGuid(), workspaceId, "photo.png", "image/png", 1000, Platform.Facebook);
        storage.SeedLocalFile(init.StorageKey, WriteImage("png", 1200, 630), "image/png");

        await svc.CompleteAsync(workspaceId, init.MediaId);

        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Equal(init.StorageKey, row.StorageKey);
        Assert.Null(row.InstagramImageStorageKey);
        Assert.Null(row.InstagramImageMimeType);
        Assert.Null(row.InstagramImageSizeBytes);
        Assert.Null(row.InstagramImageWidth);
        Assert.Null(row.InstagramImageHeight);
        Assert.Null(row.InstagramImageGeneratedAt);
        Assert.Empty(storage.UploadedObjects);
    }

    [Fact]
    public async Task CompleteAsync_JpegUpload_DoesNotCreateDerivative()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var svc = NewUploadService(db, storage);
        var workspaceId = Guid.NewGuid();

        var init = await svc.InitAsync(Guid.NewGuid(), workspaceId, "photo.jpg", "image/jpeg", 1000, Platform.Instagram);
        storage.SeedLocalFile(init.StorageKey, WriteImage("jpeg", 1080, 1080), "image/jpeg");

        await svc.CompleteAsync(workspaceId, init.MediaId);

        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Null(row.InstagramImageStorageKey);
        Assert.Empty(storage.UploadedObjects);
    }

    [Fact]
    public async Task CompleteAsync_JpgUpload_DoesNotCreateDerivative()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var svc = NewUploadService(db, storage);
        var workspaceId = Guid.NewGuid();

        var init = await svc.InitAsync(Guid.NewGuid(), workspaceId, "photo.JPG", "image/jpg", 1000, Platform.Instagram);
        storage.SeedLocalFile(init.StorageKey, WriteImage("jpeg", 1080, 1080), "image/jpg");

        await svc.CompleteAsync(workspaceId, init.MediaId);

        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Null(row.InstagramImageStorageKey);
        Assert.Empty(storage.UploadedObjects);
    }

    [Fact]
    public async Task InitAsync_WebpUpload_IsRejected()
    {
        // Final product policy: WebP is not an accepted upload type, so it never even reaches
        // the derivative step — it is rejected up front at upload init.
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var svc = NewUploadService(db, storage);
        var workspaceId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.InitAsync(Guid.NewGuid(), workspaceId, "photo.webp", "image/webp", 1000, Platform.Instagram));

        Assert.Empty(await db.Media.ToListAsync());
        Assert.Empty(storage.UploadedObjects);
    }

    [Theory]
    [InlineData("holiday.png", "holiday.jpg")]
    [InlineData("test-image.PNG", "test-image.jpg")]
    [InlineData("my.photo.v1.png", "my.photo.v1.jpg")]
    public void BuildDerivativeKey_UsesOriginalBaseNameWithJpgExtension(string originalFileName, string expectedDerivativeFileName)
    {
        var service = new InstagramDerivativeService(NullLogger<InstagramDerivativeService>.Instance);
        var key = $"users/u/workspaces/w/providers/meta-instagram/media/media-id/{originalFileName}";

        var derivativeKey = service.BuildDerivativeKey(key);

        Assert.Equal($"users/u/workspaces/w/providers/meta-instagram/media/media-id/{expectedDerivativeFileName}", derivativeKey);
    }

    [Fact]
    public async Task CompleteAsync_OverWidePng_DownscalesDerivativeAndPreservesAspectRatio()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var svc = NewUploadService(db, storage);
        var workspaceId = Guid.NewGuid();

        var init = await svc.InitAsync(Guid.NewGuid(), workspaceId, "wide.png", "image/png", 1000, Platform.Instagram);
        storage.SeedLocalFile(init.StorageKey, WriteImage("png", 2880, 1440), "image/png");

        await svc.CompleteAsync(workspaceId, init.MediaId);

        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Equal(1440, row.InstagramImageWidth);
        Assert.Equal(720, row.InstagramImageHeight);
        Assert.Equal(2.0, row.InstagramImageWidth!.Value / (double)row.InstagramImageHeight!.Value, precision: 3);
    }

    [Fact]
    public async Task CompleteAsync_DerivativeStorageFailure_CleansPartialObjectAndDoesNotPersistUploadedState()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage { ThrowAfterDerivativeWrite = true };
        var svc = NewUploadService(db, storage);
        var workspaceId = Guid.NewGuid();

        var init = await svc.InitAsync(Guid.NewGuid(), workspaceId, "photo.png", "image/png", 1000, Platform.Instagram);
        storage.SeedLocalFile(init.StorageKey, WriteImage("png", 1080, 1080), "image/png");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.CompleteAsync(workspaceId, init.MediaId));

        Assert.Contains("Instagram-ready JPEG derivative", ex.Message);
        var derivativeKey = $"{FolderOf(init.StorageKey)}/photo.jpg";
        Assert.Contains(derivativeKey, storage.DeletedKeys);
        Assert.False(storage.UploadedObjects.ContainsKey(derivativeKey));

        db.ChangeTracker.Clear();
        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Equal(MediaUploadStatus.PendingUpload, row.Status);
        Assert.Null(row.UploadedAt);
        Assert.Null(row.InstagramImageStorageKey);
    }

    private string WriteImage(string format, int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        var ext = format == "png" ? ".png" : ".jpg";
        var path = Path.Combine(Path.GetTempPath(), $"igderivative_{Guid.NewGuid():N}{ext}");
        using (var fs = File.Create(path))
        {
            if (format == "png")
                image.Save(fs, new PngEncoder());
            else
                image.Save(fs, new JpegEncoder());
        }
        _tempFiles.Add(path);
        return path;
    }

    private static string FolderOf(string storageKey)
    {
        var lastSlash = storageKey.LastIndexOf('/');
        return storageKey[..lastSlash];
    }

    private sealed class RecordingStorage : IMediaStorageProvider
    {
        private readonly Dictionary<string, (string Path, string ContentType)> _objects = new();

        public Dictionary<string, byte[]> UploadedObjects { get; } = new();
        public List<string> DeletedKeys { get; } = new();
        public bool ThrowAfterDerivativeWrite { get; init; }

        public void SeedLocalFile(string storageKey, string localPath, string contentType)
        {
            _objects[storageKey] = (localPath, contentType);
        }

        public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://example/upload/" + storageKey);

        public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://example/download/" + storageKey);

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(storageKey, out var obj))
                return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(File.OpenRead(obj.Path));
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(storageKey);
            UploadedObjects.Remove(storageKey);
            _objects.Remove(storageKey);
            return Task.CompletedTask;
        }

        public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult(_objects.TryGetValue(storageKey, out var obj) ? obj.Path : null);

        public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async Task UploadObjectAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            UploadedObjects[storageKey] = ms.ToArray();

            if (ThrowAfterDerivativeWrite)
                throw new InvalidOperationException("simulated derivative write failure");
        }

        public bool Exists(string storageKey) => _objects.ContainsKey(storageKey) || UploadedObjects.ContainsKey(storageKey);

        public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Exists(storageKey));

        public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default)
        {
            if (!_objects.TryGetValue(storageKey, out var obj))
                return Task.FromResult<StoredObjectInfo?>(null);

            var info = new FileInfo(obj.Path);
            return Task.FromResult<StoredObjectInfo?>(new StoredObjectInfo(
                info.Length,
                obj.ContentType,
                ETag: null,
                LastModified: info.LastWriteTimeUtc));
        }
    }
}
