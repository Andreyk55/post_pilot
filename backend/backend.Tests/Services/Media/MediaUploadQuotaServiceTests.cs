using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PostPilot.Api.Data;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Services.Media;

public class MediaUploadQuotaServiceTests
{
    private static MediaStorageOptions StorageOpts() => new()
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
        storageOpts: StorageOpts(),
        runMode: AppRunMode.Server,
        logger: NullLogger<MediaService>.Instance,
        uploadUrlExpiration: TimeSpan.FromMinutes(15),
        maxImageFileSizeBytes: 20 * 1024 * 1024,
        maxVideoFileSizeBytes: 200 * 1024 * 1024,
        defaultPublishingUrlExpiration: TimeSpan.FromHours(1));

    private static async Task<Guid> SeedWorkspaceAsync(AppDbContext db)
    {
        var now = DateTime.UtcNow;
        var ownerUserId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();

        db.AppUsers.Add(new Entities.AppUser
        {
            Id = ownerUserId,
            Email = $"{ownerUserId:N}@test.local",
            DisplayName = "Quota Test User",
            AuthProvider = "test",
            ExternalAuthUserId = ownerUserId.ToString("N"),
            CurrentWorkspaceId = workspaceId,
            CreatedAt = now,
            UpdatedAt = now,
        });

        db.Workspaces.Add(new Entities.Workspace
        {
            Id = workspaceId,
            Name = "Quota Test Workspace",
            OwnerUserId = ownerUserId,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync();
        return workspaceId;
    }

    [Fact]
    public async Task DisabledQuota_AllowsRequests_AndDoesNotCreateUsageRows()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        await using var db = scope.CreateDbContext();
        var quota = new MediaUploadQuotaService(db, new MediaUploadQuotaOptions
        {
            Enabled = false,
            MaxUploadsPerUserPerWindow = 2,
            WindowHours = 24,
        });

        var result = await quota.TryConsumeUploadAsync(Guid.NewGuid());

        Assert.True(result.Allowed);
        Assert.False(await db.UserMediaUploadUsages.AnyAsync());
    }

    [Fact]
    public async Task UnderLimit_ConsumesOneUpload()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        await using var db = scope.CreateDbContext();
        var userId = Guid.NewGuid();
        var quota = new MediaUploadQuotaService(db, new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 3,
            WindowHours = 24,
        });

        var result = await quota.TryConsumeUploadAsync(userId);

        Assert.True(result.Allowed);
        Assert.Equal(1, result.Used);
        Assert.Equal(2, result.Remaining);

        var row = await db.UserMediaUploadUsages.SingleAsync();
        Assert.Equal(userId, row.UserId);
        Assert.Equal(1, row.UploadCount);
    }

    [Fact]
    public async Task ExactLimitReachedByThisUpload_IsAllowed_AndRemainingBecomesZero()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var userId = Guid.NewGuid();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 2,
            WindowHours = 24,
        };

        await using (var firstDb = scope.CreateDbContext())
        {
            var quota = new MediaUploadQuotaService(firstDb, options);
            await quota.TryConsumeUploadAsync(userId);
        }

        await using var secondDb = scope.CreateDbContext();
        var secondQuota = new MediaUploadQuotaService(secondDb, options);

        var result = await secondQuota.TryConsumeUploadAsync(userId);

        Assert.True(result.Allowed);
        Assert.Equal(2, result.Used);
        Assert.Equal(0, result.Remaining);
    }

    [Fact]
    public async Task OverLimit_IsRejected()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var userId = Guid.NewGuid();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 1,
            WindowHours = 24,
        };

        await using (var firstDb = scope.CreateDbContext())
        {
            var quota = new MediaUploadQuotaService(firstDb, options);
            await quota.TryConsumeUploadAsync(userId);
        }

        await using var secondDb = scope.CreateDbContext();
        var secondQuota = new MediaUploadQuotaService(secondDb, options);
        var result = await secondQuota.TryConsumeUploadAsync(userId);

        Assert.False(result.Allowed);
        Assert.Equal(MediaUploadQuotaExceededException.DefaultErrorCode, result.ErrorCode);
        Assert.Equal(1, result.Used);
        Assert.Equal(0, result.Remaining);

        await using var verifyDb = scope.CreateDbContext();
        var usage = await verifyDb.UserMediaUploadUsages.SingleAsync(x => x.UserId == userId);
        Assert.Equal(1, usage.UploadCount);
    }

    [Fact]
    public async Task SameUserAcrossWorkspacesAndPlatforms_SharesOneBucket()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var userId = Guid.NewGuid();
        var storage = new RecordingStorage();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 2,
            WindowHours = 24,
        };

        await using (var db = scope.CreateDbContext())
        {
            var workspaceId = await SeedWorkspaceAsync(db);
            var uploadService = new MediaUploadService(
                db,
                NewMediaService(storage),
                StorageOpts(),
                NullLogger<MediaUploadService>.Instance,
                mediaUploadQuota: new MediaUploadQuotaService(db, options));

            await uploadService.InitAsync(userId, workspaceId, "photo.png", "image/png", 100, Platform.Facebook);
        }

        await using (var db = scope.CreateDbContext())
        {
            var workspaceId = await SeedWorkspaceAsync(db);
            var uploadService = new MediaUploadService(
                db,
                NewMediaService(storage),
                StorageOpts(),
                NullLogger<MediaUploadService>.Instance,
                mediaUploadQuota: new MediaUploadQuotaService(db, options));

            await uploadService.InitAsync(userId, workspaceId, "clip.mp4", "video/mp4", 100, Platform.Instagram);
        }

        await using var finalDb = scope.CreateDbContext();
        var usage = await finalDb.UserMediaUploadUsages.SingleAsync(x => x.UserId == userId);
        Assert.Equal(2, usage.UploadCount);
    }

    [Fact]
    public async Task SeparateUsers_HaveSeparateBuckets()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 1,
            WindowHours = 24,
        };

        await using var db = scope.CreateDbContext();
        var quota = new MediaUploadQuotaService(db, options);

        var userA = await quota.TryConsumeUploadAsync(Guid.NewGuid());
        var userB = await quota.TryConsumeUploadAsync(Guid.NewGuid());

        Assert.True(userA.Allowed);
        Assert.True(userB.Allowed);
        Assert.Equal(2, await db.UserMediaUploadUsages.CountAsync());
    }

    [Fact]
    public async Task ImagesAndVideosBothConsumeOne()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var userId = Guid.NewGuid();
        var storage = new RecordingStorage();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 5,
            WindowHours = 24,
        };

        await using (var db = scope.CreateDbContext())
        {
            var workspaceId = await SeedWorkspaceAsync(db);
            var uploadService = new MediaUploadService(
                db,
                NewMediaService(storage),
                StorageOpts(),
                NullLogger<MediaUploadService>.Instance,
                mediaUploadQuota: new MediaUploadQuotaService(db, options));

            await uploadService.InitAsync(userId, workspaceId, "photo.png", "image/png", 100, Platform.Facebook);
        }

        await using (var db = scope.CreateDbContext())
        {
            var workspaceId = await SeedWorkspaceAsync(db);
            var uploadService = new MediaUploadService(
                db,
                NewMediaService(storage),
                StorageOpts(),
                NullLogger<MediaUploadService>.Instance,
                mediaUploadQuota: new MediaUploadQuotaService(db, options));

            await uploadService.InitAsync(userId, workspaceId, "video.mp4", "video/mp4", 100, Platform.Instagram);
        }

        await using var verifyDb = scope.CreateDbContext();
        var usage = await verifyDb.UserMediaUploadUsages.SingleAsync(x => x.UserId == userId);
        Assert.Equal(2, usage.UploadCount);
    }

    [Fact]
    public async Task SameUserSameWindow_UsesSingleRow()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var userId = Guid.NewGuid();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 10,
            WindowHours = 24,
        };

        await using (var db = scope.CreateDbContext())
        {
            var quota = new MediaUploadQuotaService(db, options);
            await quota.TryConsumeUploadAsync(userId);
        }

        await using (var db = scope.CreateDbContext())
        {
            var quota = new MediaUploadQuotaService(db, options);
            await quota.TryConsumeUploadAsync(userId);
        }

        await using var verifyDb = scope.CreateDbContext();
        Assert.Equal(1, await verifyDb.UserMediaUploadUsages.CountAsync(x => x.UserId == userId));
        Assert.Equal(2, await verifyDb.UserMediaUploadUsages.Where(x => x.UserId == userId).Select(x => x.UploadCount).SingleAsync());
    }

    [Fact]
    public async Task RejectedInit_DoesNotCreateMediaRow()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var userId = Guid.NewGuid();
        var storage = new RecordingStorage();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 1,
            WindowHours = 24,
        };

        await using (var db = scope.CreateDbContext())
        {
            var workspaceId = await SeedWorkspaceAsync(db);
            var uploadService = new MediaUploadService(
                db,
                NewMediaService(storage),
                StorageOpts(),
                NullLogger<MediaUploadService>.Instance,
                mediaUploadQuota: new MediaUploadQuotaService(db, options));

            await uploadService.InitAsync(userId, workspaceId, "photo.png", "image/png", 100, Platform.Facebook);
        }

        await using (var db = scope.CreateDbContext())
        {
            var workspaceId = await SeedWorkspaceAsync(db);
            var uploadService = new MediaUploadService(
                db,
                NewMediaService(storage),
                StorageOpts(),
                NullLogger<MediaUploadService>.Instance,
                mediaUploadQuota: new MediaUploadQuotaService(db, options));

            await Assert.ThrowsAsync<MediaUploadQuotaExceededException>(() =>
                uploadService.InitAsync(userId, workspaceId, "video.mp4", "video/mp4", 100, Platform.Instagram));
        }

        await using var verifyDb = scope.CreateDbContext();
        Assert.Single(await verifyDb.Media.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentRequests_DoNotExceedLimit_WhenProviderSupportsTransactions()
    {
        await using var scope = await SqliteDbScope.CreateAsync();
        var userId = Guid.NewGuid();
        var options = new MediaUploadQuotaOptions
        {
            Enabled = true,
            MaxUploadsPerUserPerWindow = 3,
            WindowHours = 24,
        };

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = scope.CreateDbContext();
            var quota = new MediaUploadQuotaService(db, options);
            return await quota.TryConsumeUploadAsync(userId);
        });

        var results = await Task.WhenAll(tasks);

        Assert.True(results.Count(x => x.Allowed) <= 3);

        await using var verifyDb = scope.CreateDbContext();
        var usage = await verifyDb.UserMediaUploadUsages.SingleAsync(x => x.UserId == userId);
        Assert.True(usage.UploadCount <= 3);
    }

    private sealed class RecordingStorage : IMediaStorageProvider
    {
        public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://example/upload/" + storageKey);
        public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
            => Task.FromResult("https://example/download/" + storageKey);
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UploadObjectAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool Exists(string storageKey) => false;
        public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default) => Task.FromResult<StoredObjectInfo?>(null);
    }

    private sealed class SqliteDbScope : IAsyncDisposable
    {
        private readonly SqliteConnection _keepAliveConnection;
        private readonly string _connectionString;

        private SqliteDbScope(SqliteConnection keepAliveConnection, string connectionString)
        {
            _keepAliveConnection = keepAliveConnection;
            _connectionString = connectionString;
        }

        public static async Task<SqliteDbScope> CreateAsync()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = $"quota-tests-{Guid.NewGuid():N}",
                Mode = SqliteOpenMode.Memory,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            var keepAliveConnection = new SqliteConnection(connectionString);
            await keepAliveConnection.OpenAsync();

            var scope = new SqliteDbScope(keepAliveConnection, connectionString);
            await using var db = scope.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            return scope;
        }

        public AppDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new AppDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await _keepAliveConnection.DisposeAsync();
        }
    }
}
