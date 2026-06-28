using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

public class MediaUploadServiceVideoThumbnailTests : IDisposable
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

    [Fact]
    public async Task CompleteAsync_VideoUpload_StoresGeneratedThumbnailMetadata()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var logger = new ListLogger<MediaUploadService>();
        var thumbnailGenerator = new FakeVideoThumbnailGenerator();
        var service = NewUploadService(db, storage, logger, thumbnailGenerator);
        var workspaceId = Guid.NewGuid();

        var init = await service.InitAsync(Guid.NewGuid(), workspaceId, "clip.mp4", "video/mp4", 1024, Platform.Facebook);
        storage.SeedLocalFile(init.StorageKey, WriteTempFile(".mp4", new byte[] { 1, 2, 3, 4 }), "video/mp4");

        await service.CompleteAsync(workspaceId, init.MediaId);

        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Equal(MediaUploadStatus.Uploaded, row.Status);
        Assert.Equal($"{FolderOf(init.StorageKey)}/thumbnail.jpg", row.ThumbnailStorageKey);
        Assert.Equal("image/jpeg", row.ThumbnailMimeType);
        Assert.Equal(480, row.ThumbnailWidth);
        Assert.Equal(270, row.ThumbnailHeight);
        Assert.Equal(12345, row.ThumbnailSizeBytes);
        Assert.NotNull(row.ThumbnailCreatedAtUtc);
        Assert.Equal(1, thumbnailGenerator.CallCount);
        Assert.Contains(row.ThumbnailStorageKey!, storage.UploadedObjects.Keys);
    }

    [Fact]
    public async Task CompleteAsync_ThumbnailFailure_IsNonFatalAndLogsWarning()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var logger = new ListLogger<MediaUploadService>();
        var thumbnailGenerator = new FakeVideoThumbnailGenerator { ThrowOnGenerate = true };
        var service = NewUploadService(db, storage, logger, thumbnailGenerator);
        var workspaceId = Guid.NewGuid();

        var init = await service.InitAsync(Guid.NewGuid(), workspaceId, "clip.mp4", "video/mp4", 1024, Platform.Instagram);
        storage.SeedLocalFile(init.StorageKey, WriteTempFile(".mp4", new byte[] { 9, 8, 7, 6 }), "video/mp4");

        var result = await service.CompleteAsync(workspaceId, init.MediaId);

        Assert.Equal(init.MediaId, result.MediaId);

        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Equal(MediaUploadStatus.Uploaded, row.Status);
        Assert.Null(row.ThumbnailStorageKey);
        Assert.Null(row.ThumbnailMimeType);
        Assert.Null(row.ThumbnailWidth);
        Assert.Null(row.ThumbnailHeight);
        Assert.Null(row.ThumbnailSizeBytes);
        Assert.Null(row.ThumbnailCreatedAtUtc);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("Video thumbnail generation failed", StringComparison.Ordinal)
            && entry.Message.Contains(init.MediaId.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_ImageUpload_DoesNotCallVideoThumbnailGenerator()
    {
        await using var db = NewDb();
        var storage = new RecordingStorage();
        var thumbnailGenerator = new FakeVideoThumbnailGenerator();
        var service = NewUploadService(db, storage, new ListLogger<MediaUploadService>(), thumbnailGenerator);
        var workspaceId = Guid.NewGuid();

        var init = await service.InitAsync(Guid.NewGuid(), workspaceId, "photo.jpg", "image/jpeg", 1024, Platform.Facebook);
        storage.SeedLocalFile(init.StorageKey, WriteTempFile(".jpg", new byte[] { 1, 2, 3 }), "image/jpeg");

        await service.CompleteAsync(workspaceId, init.MediaId);

        Assert.Equal(0, thumbnailGenerator.CallCount);
        var row = await db.Media.SingleAsync(m => m.Id == init.MediaId);
        Assert.Null(row.ThumbnailStorageKey);
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
        logger: new ListLogger<MediaService>(),
        uploadUrlExpiration: TimeSpan.FromMinutes(15),
        maxImageFileSizeBytes: 20 * 1024 * 1024,
        maxVideoFileSizeBytes: 200 * 1024 * 1024,
        publishingBaseUrl: "https://post-pilot.cloud-ip.cc",
        defaultPublishingUrlExpiration: TimeSpan.FromHours(1));

    private static MediaUploadService NewUploadService(
        AppDbContext db,
        RecordingStorage storage,
        ILogger<MediaUploadService> logger,
        IVideoThumbnailGenerator thumbnailGenerator) => new(
            db,
            NewMediaService(storage),
            Opts(),
            logger,
            derivativeService: null,
            videoThumbnailGenerator: thumbnailGenerator);

    private string WriteTempFile(string extension, byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"postpilot-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    private static string FolderOf(string storageKey)
    {
        var lastSlash = storageKey.LastIndexOf('/');
        return storageKey[..lastSlash];
    }

    private sealed class FakeVideoThumbnailGenerator : IVideoThumbnailGenerator
    {
        public int CallCount { get; private set; }
        public bool ThrowOnGenerate { get; init; }

        public Task<VideoThumbnailResult> GenerateAsync(
            string sourceVideoPath,
            string outputImagePath,
            int maxWidth,
            CancellationToken cancellationToken = default)
        {
            CallCount++;

            if (ThrowOnGenerate)
                throw new InvalidOperationException("simulated ffmpeg failure");

            File.WriteAllBytes(outputImagePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            return Task.FromResult(new VideoThumbnailResult("image/jpeg", 480, 270, 12345));
        }
    }

    private sealed class RecordingStorage : IMediaStorageProvider
    {
        private readonly Dictionary<string, (string Path, string ContentType)> _objects = new();

        public Dictionary<string, byte[]> UploadedObjects { get; } = new();

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

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}