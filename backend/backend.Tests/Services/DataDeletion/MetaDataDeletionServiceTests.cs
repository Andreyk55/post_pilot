using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.DataDeletion;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Tests.TestHelpers;
using Xunit;

namespace PostPilot.Api.Tests.Services.DataDeletion;

/// <summary>
/// Formal Meta purge behavior: lookup, idempotency, full Meta-scoped deletion,
/// storage prefix-guarded best-effort, and strict isolation from other
/// workspaces / users / non-Meta data.
/// </summary>
public class MetaDataDeletionServiceTests : IDisposable
{
    private static readonly Guid UserAId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid UserBId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid WorkspaceAId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceBId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private const string AccountAlpha = "meta-user-alpha";
    private const string AccountBeta = "meta-user-beta";

    private readonly AppDbContext _db;
    private readonly Mock<IPostScheduler> _scheduler = new();
    private readonly RecordingStorageProvider _storage = new();
    private readonly MetaDataDeletionService _service;

    public MetaDataDeletionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        SeedUsersAndWorkspaces();

        _scheduler.Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var storageDeletion = new StorageDeletionService(_storage, NullLogger<StorageDeletionService>.Instance);
        _service = new MetaDataDeletionService(
            _db, _scheduler.Object, storageDeletion, NullLogger<MetaDataDeletionService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // ── Seeding ───────────────────────────────────────────────────────────────

    private void SeedUsersAndWorkspaces()
    {
        var now = DateTime.UtcNow;
        _db.AppUsers.AddRange(
            new AppUser { Id = UserAId, Email = "a@t", DisplayName = "A", AuthProvider = "google", ExternalAuthUserId = "a", CreatedAt = now, UpdatedAt = now },
            new AppUser { Id = UserBId, Email = "b@t", DisplayName = "B", AuthProvider = "google", ExternalAuthUserId = "b", CreatedAt = now, UpdatedAt = now });
        _db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceAId, Name = "A", OwnerUserId = UserAId, CreatedAt = now, UpdatedAt = now },
            new Workspace { Id = WorkspaceBId, Name = "B", OwnerUserId = UserBId, CreatedAt = now, UpdatedAt = now });
        _db.SaveChanges();
    }

    private string FbKey(Guid userId, Guid wsId, string name) =>
        $"users/{userId:D}/workspaces/{wsId:D}/providers/meta-facebook/media/{Guid.NewGuid():D}/{name}";

    private string IgKey(Guid userId, Guid wsId, string name) =>
        $"users/{userId:D}/workspaces/{wsId:D}/providers/meta-instagram/media/{Guid.NewGuid():D}/{name}";

    private (MetaConnection conn, ConnectedPage page, ConnectedInstagramAccount ig) SeedMeta(
        Guid wsId, Guid userId, string account, bool connected = true)
    {
        var now = DateTime.UtcNow;
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, UserId = userId, Provider = ProviderType.Meta,
            ProviderAccountId = account, ProviderAccountName = account, AccessToken = connected ? "tok" : null,
            TokenExpiresAt = now.AddDays(30), ConnectedAt = now, UpdatedAt = now,
            IsConnected = connected, DisconnectedAt = connected ? null : now,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, MetaConnectionId = conn.Id,
            PageId = $"page-{account}", Name = "Page", AccessToken = "ptok", CreatedAt = now, IsConnected = connected,
        };
        var ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, MetaConnectionId = conn.Id,
            IgBusinessId = $"ig-{account}", Username = "u", PageId = page.PageId, PageName = page.Name,
            CreatedAt = now, IsConnected = connected,
        };
        _db.MetaConnections.Add(conn);
        _db.ConnectedPages.Add(page);
        _db.ConnectedInstagramAccounts.Add(ig);
        _db.SaveChanges();
        return (conn, page, ig);
    }

    private Post SeedPost(Guid wsId, Guid pageId, PostStatus status, Platform platform = Platform.Facebook, string? mediaUrl = null, string? scheduleArn = null)
    {
        var p = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, Content = "c", Platform = platform,
            TargetPageId = pageId, ScheduledAt = DateTime.UtcNow.AddHours(1), Status = status,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, MediaUrl = mediaUrl, ScheduleArn = scheduleArn,
            PublishedAt = status == PostStatus.Published ? DateTime.UtcNow : null,
        };
        _db.Posts.Add(p);
        _db.SaveChanges();
        return p;
    }

    private Entities.Media SeedMedia(Guid wsId, string storageKey, string? igDerivativeKey = null)
    {
        var m = new Entities.Media
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, StorageProvider = "s3", Bucket = "b",
            StorageKey = storageKey, InstagramImageStorageKey = igDerivativeKey,
            OriginalFileName = "f.jpg", ContentType = "image/jpeg", Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Media.Add(m);
        _db.SaveChanges();
        return m;
    }

    // ── Lookup / idempotency ───────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_account_returns_already_deleted_noop()
    {
        var result = await _service.PurgeByProviderAccountIdAsync("nobody", CancellationToken.None);
        Assert.Equal(DataDeletionStatus.AlreadyDeleted, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Null_or_empty_account_is_safe_noop(string? account)
    {
        var result = await _service.PurgeByProviderAccountIdAsync(account, CancellationToken.None);
        Assert.Equal(DataDeletionStatus.AlreadyDeleted, result.Status);
    }

    [Fact]
    public async Task Finds_connection_by_provider_and_account_id_even_when_disconnected()
    {
        SeedMeta(WorkspaceAId, UserAId, AccountAlpha, connected: false);

        var result = await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        Assert.Equal(DataDeletionStatus.Completed, result.Status);
        Assert.Equal(WorkspaceAId, result.WorkspaceId);
        Assert.Equal(UserAId, result.UserId);
        Assert.False(await _db.MetaConnections.AnyAsync(c => c.ProviderAccountId == AccountAlpha));
    }

    [Fact]
    public async Task Ignores_non_meta_providers_with_same_account_id()
    {
        // A LinkedIn row carrying the same external id must NOT be matched or deleted.
        var now = DateTime.UtcNow;
        _db.MetaConnections.Add(new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, UserId = UserAId, Provider = ProviderType.LinkedIn,
            ProviderAccountId = AccountAlpha, ConnectedAt = now, UpdatedAt = now, IsConnected = true,
        });
        _db.SaveChanges();

        var result = await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        Assert.Equal(DataDeletionStatus.AlreadyDeleted, result.Status);
        Assert.True(await _db.MetaConnections.AnyAsync(c => c.Provider == ProviderType.LinkedIn));
    }

    [Fact]
    public async Task Second_purge_call_is_noop_success()
    {
        SeedMeta(WorkspaceAId, UserAId, AccountAlpha);

        var first = await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);
        var second = await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        Assert.Equal(DataDeletionStatus.Completed, first.Status);
        Assert.Equal(DataDeletionStatus.AlreadyDeleted, second.Status);
    }

    // ── Full purge ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Purges_all_meta_rows_across_every_status_without_touching_workspace_or_user()
    {
        var (conn, page, ig) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        var scheduled = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled, scheduleArn: "arn:1");
        var published = SeedPost(WorkspaceAId, page.Id, PostStatus.Published);
        var failed = SeedPost(WorkspaceAId, page.Id, PostStatus.Failed);
        var canceled = SeedPost(WorkspaceAId, page.Id, PostStatus.Canceled);
        // A carousel media item under the scheduled post.
        _db.PostMediaItems.Add(new PostMediaItem
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, PostId = scheduled.Id, Order = 0,
            MediaUrl = FbKey(UserAId, WorkspaceAId, "carousel.jpg"), MediaType = MediaType.Image,
        });
        _db.MetaOAuthStates.Add(new MetaOAuthState
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, State = "s", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        _db.SaveChanges();

        var result = await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        Assert.Equal(DataDeletionStatus.Completed, result.Status);

        // All Meta rows gone — every post status included.
        Assert.False(await _db.MetaConnections.AnyAsync(c => c.Id == conn.Id));
        Assert.False(await _db.ConnectedPages.AnyAsync(p => p.Id == page.Id));
        Assert.False(await _db.ConnectedInstagramAccounts.AnyAsync(i => i.Id == ig.Id));
        Assert.Empty(await _db.Posts.Where(p => p.WorkspaceId == WorkspaceAId).ToListAsync());
        Assert.Empty(await _db.PostMediaItems.Where(p => p.WorkspaceId == WorkspaceAId).ToListAsync());
        Assert.Empty(await _db.MetaOAuthStates.Where(s => s.WorkspaceId == WorkspaceAId).ToListAsync());
        _ = new[] { published.Id, failed.Id, canceled.Id }; // documented: all deleted regardless of status

        // Workspace + user survive.
        Assert.True(await _db.Workspaces.AnyAsync(w => w.Id == WorkspaceAId));
        Assert.True(await _db.AppUsers.AnyAsync(u => u.Id == UserAId));
    }

    [Fact]
    public async Task Cancels_pending_schedules_before_deletion()
    {
        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        var scheduled = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled, scheduleArn: "arn:1");
        var published = SeedPost(WorkspaceAId, page.Id, PostStatus.Published);

        await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        _scheduler.Verify(s => s.CancelScheduleAsync(
            It.Is<Post>(p => p.Id == scheduled.Id), It.IsAny<CancellationToken>()), Times.Once);
        // Published post has no active schedule → not canceled.
        _scheduler.Verify(s => s.CancelScheduleAsync(
            It.Is<Post>(p => p.Id == published.Id), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Missing_schedule_does_not_break_idempotency()
    {
        _scheduler.Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("schedule already gone"));

        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled, scheduleArn: "arn:1");

        var result = await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        Assert.Equal(DataDeletionStatus.Completed, result.Status);
        Assert.Empty(await _db.Posts.Where(p => p.WorkspaceId == WorkspaceAId).ToListAsync());
    }

    // ── Media / storage ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Deletes_meta_media_rows_and_storage_objects_including_ig_derivative()
    {
        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        var fbKey = FbKey(UserAId, WorkspaceAId, "fb.png");
        var igDeriv = fbKey.Replace(".png", ".jpg");
        var igKey = IgKey(UserAId, WorkspaceAId, "ig.jpg");
        var metaFb = SeedMedia(WorkspaceAId, fbKey, igDerivativeKey: igDeriv);
        var metaIg = SeedMedia(WorkspaceAId, igKey);
        SeedPost(WorkspaceAId, page.Id, PostStatus.Published, mediaUrl: fbKey);

        await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        // Media rows deleted.
        Assert.False(await _db.Media.AnyAsync(m => m.Id == metaFb.Id));
        Assert.False(await _db.Media.AnyAsync(m => m.Id == metaIg.Id));

        // Storage objects attempted: original FB key, IG derivative key, IG key.
        Assert.Contains(fbKey, _storage.DeletedKeys);
        Assert.Contains(igDeriv, _storage.DeletedKeys);
        Assert.Contains(igKey, _storage.DeletedKeys);
    }

    [Fact]
    public async Task Does_not_delete_non_meta_media_in_same_workspace()
    {
        var (_, _, _) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        var legacy = SeedMedia(WorkspaceAId, "media/legacy.jpg"); // not under a meta provider prefix

        await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        Assert.True(await _db.Media.AnyAsync(m => m.Id == legacy.Id));
        Assert.DoesNotContain("media/legacy.jpg", _storage.DeletedKeys);
    }

    [Fact]
    public async Task Storage_failure_does_not_abort_db_purge()
    {
        _storage.ThrowOnDelete = _ => true; // every storage delete fails

        var (conn, page, _) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        SeedMedia(WorkspaceAId, FbKey(UserAId, WorkspaceAId, "x.jpg"));
        SeedPost(WorkspaceAId, page.Id, PostStatus.Published);

        var result = await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        Assert.Equal(DataDeletionStatus.Completed, result.Status);
        Assert.NotEmpty(result.Warnings);
        Assert.False(await _db.MetaConnections.AnyAsync(c => c.Id == conn.Id));
        Assert.Empty(await _db.Media.Where(m => m.WorkspaceId == WorkspaceAId).ToListAsync());
    }

    // ── Isolation ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Does_not_touch_other_workspace_data()
    {
        var (_, pageA, _) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        var (connB, pageB, igB) = SeedMeta(WorkspaceBId, UserBId, AccountBeta);
        SeedPost(WorkspaceAId, pageA.Id, PostStatus.Published);
        var bPost = SeedPost(WorkspaceBId, pageB.Id, PostStatus.Published);
        var bMedia = SeedMedia(WorkspaceBId, FbKey(UserBId, WorkspaceBId, "b.jpg"));

        await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        // B is completely untouched.
        Assert.True(await _db.MetaConnections.AnyAsync(c => c.Id == connB.Id));
        Assert.True(await _db.ConnectedPages.AnyAsync(p => p.Id == pageB.Id));
        Assert.True(await _db.ConnectedInstagramAccounts.AnyAsync(i => i.Id == igB.Id));
        Assert.True(await _db.Posts.AnyAsync(p => p.Id == bPost.Id));
        Assert.True(await _db.Media.AnyAsync(m => m.Id == bMedia.Id));
        Assert.DoesNotContain(bMedia.StorageKey, _storage.DeletedKeys);
    }

    [Fact]
    public async Task After_formal_deletion_no_history_resurfaces()
    {
        var (_, page, _) = SeedMeta(WorkspaceAId, UserAId, AccountAlpha);
        SeedPost(WorkspaceAId, page.Id, PostStatus.Published);
        SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled);

        await _service.PurgeByProviderAccountIdAsync(AccountAlpha, CancellationToken.None);

        // Nothing left to resurface on reconnect: no connection, no pages, no posts.
        Assert.False(await _db.MetaConnections.AnyAsync(c => c.ProviderAccountId == AccountAlpha));
        Assert.Empty(await _db.ConnectedPages.Where(p => p.WorkspaceId == WorkspaceAId).ToListAsync());
        Assert.Empty(await _db.Posts.Where(p => p.WorkspaceId == WorkspaceAId).ToListAsync());
    }
}
