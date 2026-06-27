using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Account;
using PostPilot.Api.Services.DataDeletion;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Tests.TestHelpers;
using Xunit;

namespace PostPilot.Api.Tests.Services.Account;

/// <summary>
/// Full account deletion: removes the authenticated user's AppUser/auth identity,
/// owned workspaces, and all data inside them — and ONLY that user's data.
/// </summary>
public class AccountDeletionServiceTests : IDisposable
{
    private static readonly Guid UserAId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid UserBId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid WorkspaceAId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceBId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private readonly AppDbContext _db;
    private readonly Mock<IPostScheduler> _scheduler = new();
    private readonly RecordingStorageProvider _storage = new();
    private readonly AccountDeletionService _service;

    public AccountDeletionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        Seed();

        _scheduler.Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var storageDeletion = new StorageDeletionService(_storage, NullLogger<StorageDeletionService>.Instance);
        _service = new AccountDeletionService(
            _db, _scheduler.Object, storageDeletion, NullLogger<AccountDeletionService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private void Seed()
    {
        var now = DateTime.UtcNow;
        _db.AppUsers.AddRange(
            new AppUser { Id = UserAId, Email = "a@t", DisplayName = "A", AuthProvider = "google", ExternalAuthUserId = "a", CurrentWorkspaceId = WorkspaceAId, CreatedAt = now, UpdatedAt = now },
            new AppUser { Id = UserBId, Email = "b@t", DisplayName = "B", AuthProvider = "google", ExternalAuthUserId = "b", CurrentWorkspaceId = WorkspaceBId, CreatedAt = now, UpdatedAt = now });
        _db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceAId, Name = "A", OwnerUserId = UserAId, CreatedAt = now, UpdatedAt = now },
            new Workspace { Id = WorkspaceBId, Name = "B", OwnerUserId = UserBId, CreatedAt = now, UpdatedAt = now });
        _db.WorkspaceMembers.AddRange(
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, UserId = UserAId, Role = WorkspaceRole.Owner, CreatedAt = now },
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId, UserId = UserBId, Role = WorkspaceRole.Owner, CreatedAt = now });
        _db.SaveChanges();
    }

    private string FbKey(Guid userId, Guid wsId, string name) =>
        $"users/{userId:D}/workspaces/{wsId:D}/providers/meta-facebook/media/{Guid.NewGuid():D}/{name}";

    private (MetaConnection conn, ConnectedPage page) SeedMeta(Guid wsId, Guid userId, string account)
    {
        var now = DateTime.UtcNow;
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, UserId = userId, Provider = ProviderType.Meta,
            ProviderAccountId = account, ProviderAccountName = account, AccessToken = "tok",
            TokenExpiresAt = now.AddDays(30), ConnectedAt = now, UpdatedAt = now, IsConnected = true,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, MetaConnectionId = conn.Id,
            PageId = $"page-{account}", Name = "P", AccessToken = "ptok", CreatedAt = now, IsConnected = true,
        };
        _db.MetaConnections.Add(conn);
        _db.ConnectedPages.Add(page);
        _db.SaveChanges();
        return (conn, page);
    }

    private Post SeedPost(Guid wsId, Guid pageId, PostStatus status, string? scheduleArn = null, string? mediaUrl = null)
    {
        var p = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, Content = "c", Platform = Platform.Facebook,
            TargetPageId = pageId, ScheduledAt = DateTime.UtcNow.AddHours(1), Status = status,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, ScheduleArn = scheduleArn, MediaUrl = mediaUrl,
        };
        _db.Posts.Add(p);
        _db.SaveChanges();
        return p;
    }

    private Entities.Media SeedMedia(Guid wsId, string key)
    {
        var m = new Entities.Media
        {
            Id = Guid.NewGuid(), WorkspaceId = wsId, StorageProvider = "s3", Bucket = "b", StorageKey = key,
            OriginalFileName = "f", ContentType = "image/jpeg", Status = MediaUploadStatus.Uploaded, CreatedAt = DateTime.UtcNow,
        };
        _db.Media.Add(m);
        _db.SaveChanges();
        return m;
    }

    private SupportContactRequest SeedSupportRequest(Guid userId, Guid? workspaceId)
    {
        var r = new SupportContactRequest
        {
            Id = Guid.NewGuid(), UserId = userId, WorkspaceId = workspaceId,
            Subject = "Help", Message = "Please", Status = SupportContactStatus.New, CreatedAt = DateTime.UtcNow,
        };
        _db.SupportContactRequests.Add(r);
        _db.SaveChanges();
        return r;
    }

    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deletes_authenticated_users_user_row_and_owned_workspace()
    {
        await _service.DeleteCurrentAccountAsync(UserAId, CancellationToken.None);

        Assert.False(await _db.AppUsers.AnyAsync(u => u.Id == UserAId));
        Assert.False(await _db.Workspaces.AnyAsync(w => w.Id == WorkspaceAId));
        Assert.False(await _db.WorkspaceMembers.AnyAsync(m => m.UserId == UserAId));
    }

    [Fact]
    public async Task Deletes_all_owned_provider_meta_posts_and_media()
    {
        var (conn, page) = SeedMeta(WorkspaceAId, UserAId, "alpha");
        var fbKey = FbKey(UserAId, WorkspaceAId, "a.jpg");
        SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled, scheduleArn: "arn:1");
        SeedPost(WorkspaceAId, page.Id, PostStatus.Published, mediaUrl: fbKey);
        var media = SeedMedia(WorkspaceAId, fbKey);

        await _service.DeleteCurrentAccountAsync(UserAId, CancellationToken.None);

        Assert.False(await _db.MetaConnections.AnyAsync(c => c.Id == conn.Id));
        Assert.False(await _db.ConnectedPages.AnyAsync(p => p.Id == page.Id));
        Assert.Empty(await _db.Posts.Where(p => p.WorkspaceId == WorkspaceAId).ToListAsync());
        Assert.False(await _db.Media.AnyAsync(m => m.Id == media.Id));
        Assert.Contains(fbKey, _storage.DeletedKeys);
    }

    [Fact]
    public async Task Cancels_pending_schedules()
    {
        var (_, page) = SeedMeta(WorkspaceAId, UserAId, "alpha");
        var scheduled = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled, scheduleArn: "arn:1");

        await _service.DeleteCurrentAccountAsync(UserAId, CancellationToken.None);

        _scheduler.Verify(s => s.CancelScheduleAsync(
            It.Is<Post>(p => p.Id == scheduled.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Does_not_delete_other_users_data()
    {
        var (connB, pageB) = SeedMeta(WorkspaceBId, UserBId, "beta");
        var bPost = SeedPost(WorkspaceBId, pageB.Id, PostStatus.Published);
        var bMedia = SeedMedia(WorkspaceBId, FbKey(UserBId, WorkspaceBId, "b.jpg"));

        await _service.DeleteCurrentAccountAsync(UserAId, CancellationToken.None);

        // User B and all their data untouched.
        Assert.True(await _db.AppUsers.AnyAsync(u => u.Id == UserBId));
        Assert.True(await _db.Workspaces.AnyAsync(w => w.Id == WorkspaceBId));
        Assert.True(await _db.MetaConnections.AnyAsync(c => c.Id == connB.Id));
        Assert.True(await _db.Posts.AnyAsync(p => p.Id == bPost.Id));
        Assert.True(await _db.Media.AnyAsync(m => m.Id == bMedia.Id));
        Assert.True(await _db.WorkspaceMembers.AnyAsync(m => m.UserId == UserBId));
        Assert.DoesNotContain(bMedia.StorageKey, _storage.DeletedKeys);
    }

    [Fact]
    public async Task Deletes_authenticated_users_support_requests()
    {
        var mine = SeedSupportRequest(UserAId, WorkspaceAId);
        var minePlain = SeedSupportRequest(UserAId, workspaceId: null);

        await _service.DeleteCurrentAccountAsync(UserAId, CancellationToken.None);

        Assert.False(await _db.SupportContactRequests.AnyAsync(r => r.Id == mine.Id));
        Assert.False(await _db.SupportContactRequests.AnyAsync(r => r.Id == minePlain.Id));
    }

    [Fact]
    public async Task Does_not_delete_other_users_support_requests()
    {
        var mine = SeedSupportRequest(UserAId, WorkspaceAId);
        var theirs = SeedSupportRequest(UserBId, WorkspaceBId);

        await _service.DeleteCurrentAccountAsync(UserAId, CancellationToken.None);

        Assert.False(await _db.SupportContactRequests.AnyAsync(r => r.Id == mine.Id));
        Assert.True(await _db.SupportContactRequests.AnyAsync(r => r.Id == theirs.Id));
    }

    [Fact]
    public async Task Deleting_missing_user_is_idempotent_noop()
    {
        await _service.DeleteCurrentAccountAsync(Guid.NewGuid(), CancellationToken.None);
        // No throw; existing users intact.
        Assert.True(await _db.AppUsers.AnyAsync(u => u.Id == UserAId));
    }

    [Fact]
    public async Task Removes_user_membership_in_workspaces_owned_by_others()
    {
        // User A is also a guest member of workspace B (owned by B).
        _db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId, UserId = UserAId, Role = WorkspaceRole.Member, CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        await _service.DeleteCurrentAccountAsync(UserAId, CancellationToken.None);

        // A's guest membership is gone, but B's workspace and B's own membership remain.
        Assert.False(await _db.WorkspaceMembers.AnyAsync(m => m.UserId == UserAId));
        Assert.True(await _db.Workspaces.AnyAsync(w => w.Id == WorkspaceBId));
        Assert.True(await _db.WorkspaceMembers.AnyAsync(m => m.UserId == UserBId && m.WorkspaceId == WorkspaceBId));
    }
}
