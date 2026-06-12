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
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// Workspace NAME is display-only. It must never gate authorization or be used to
/// look up / join data. Two different PostPilot users may each own a workspace with
/// the SAME display name, and that name collision must cause ZERO data sharing.
///
/// These drive the real <see cref="WorkspacesController"/> (create/list/switch) and
/// the real <see cref="CurrentWorkspaceProvider"/> against an in-memory DB, with no
/// pre-seeded workspaces — proving the MVP "one user per workspace, name not unique,
/// id-only authorization" rules end to end.
///
/// Spec mapping (section G):
///   G1 — same name, different users → both allowed, separate ids.
///   G3 — same name, isolation → each user sees only their own workspace's data.
/// Plus: name is not globally unique, and the owner is the sole member.
/// </summary>
public class WorkspaceNameIsolationTests : IDisposable
{
    private static readonly Guid User1Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid User2Id = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private const string SharedName = "My Brand";

    private readonly AppDbContext _db;

    public WorkspaceNameIsolationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        SeedUsers();
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private void SeedUsers()
    {
        var now = DateTime.UtcNow;
        _db.AppUsers.AddRange(
            new AppUser
            {
                Id = User1Id, Email = "u1@test", DisplayName = "User One",
                AuthProvider = "google", ExternalAuthUserId = "u1-sub",
                CreatedAt = now, UpdatedAt = now,
            },
            new AppUser
            {
                Id = User2Id, Email = "u2@test", DisplayName = "User Two",
                AuthProvider = "google", ExternalAuthUserId = "u2-sub",
                CreatedAt = now, UpdatedAt = now,
            });
        _db.SaveChanges();
    }

    private WorkspacesController ControllerFor(Guid userId)
    {
        var user = new Mock<ICurrentUserProvider>();
        user.Setup(u => u.GetCurrentUserId()).Returns(userId);
        return new WorkspacesController(_db, user.Object, NullLogger<WorkspacesController>.Instance);
    }

    private CurrentWorkspaceProvider ResolverFor(Guid userId)
    {
        var user = new Mock<ICurrentUserProvider>();
        user.Setup(u => u.GetCurrentUserId()).Returns(userId);
        return new CurrentWorkspaceProvider(_db, user.Object, NullLogger<CurrentWorkspaceProvider>.Instance);
    }

    private static Guid IdOf(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var idProp = ok.Value!.GetType().GetProperty("id")!;
        return (Guid)idProp.GetValue(ok.Value)!;
    }

    // ── G1: same name, different users → both allowed, separate ids ───────────────

    [Fact]
    public async Task Two_users_can_create_workspaces_with_the_same_name()
    {
        var ws1Id = IdOf(await ControllerFor(User1Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));
        var ws2Id = IdOf(await ControllerFor(User2Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));

        // Both created (no global-unique-name rejection) and they are DISTINCT ids.
        Assert.NotEqual(Guid.Empty, ws1Id);
        Assert.NotEqual(Guid.Empty, ws2Id);
        Assert.NotEqual(ws1Id, ws2Id);

        // Two rows exist with the identical name, owned by different users.
        var rows = await _db.Workspaces.Where(w => w.Name == SharedName).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, w => w.Id == ws1Id && w.OwnerUserId == User1Id);
        Assert.Contains(rows, w => w.Id == ws2Id && w.OwnerUserId == User2Id);
    }

    [Fact]
    public async Task Workspace_name_is_not_globally_unique_in_the_schema()
    {
        // Belt-and-suspenders: there must be NO unique index on Name. Insert two rows
        // with the same name directly and confirm SaveChanges does not throw.
        var now = DateTime.UtcNow;
        _db.Workspaces.AddRange(
            new Workspace { Id = Guid.NewGuid(), Name = SharedName, OwnerUserId = User1Id, CreatedAt = now, UpdatedAt = now },
            new Workspace { Id = Guid.NewGuid(), Name = SharedName, OwnerUserId = User2Id, CreatedAt = now, UpdatedAt = now });

        var ex = await Record.ExceptionAsync(() => _db.SaveChangesAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task Created_workspace_has_exactly_one_member_the_owner()
    {
        var wsId = IdOf(await ControllerFor(User1Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));

        var members = await _db.WorkspaceMembers.Where(m => m.WorkspaceId == wsId).ToListAsync();
        var single = Assert.Single(members);
        Assert.Equal(User1Id, single.UserId);
        Assert.Equal(WorkspaceRole.Owner, single.Role);
    }

    // ── G3: same name, isolation → each user only sees their own ──────────────────

    [Fact]
    public async Task Same_named_workspaces_stay_isolated_per_user()
    {
        var ws1Id = IdOf(await ControllerFor(User1Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));
        var ws2Id = IdOf(await ControllerFor(User2Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));

        // Each user's List shows ONLY their own same-named workspace.
        var list1 = Assert.IsType<OkObjectResult>(await ControllerFor(User1Id).List(CancellationToken.None));
        var list2 = Assert.IsType<OkObjectResult>(await ControllerFor(User2Id).List(CancellationToken.None));

        Assert.Single((System.Collections.IEnumerable)list1.Value!);
        Assert.Single((System.Collections.IEnumerable)list2.Value!);

        // The resolver, keyed on UserId + the user's selected id, returns the right
        // workspace despite the identical names.
        var info1 = await ResolverFor(User1Id).GetCurrentWorkspaceAsync();
        var info2 = await ResolverFor(User2Id).GetCurrentWorkspaceAsync();
        Assert.Equal(ws1Id, info1.WorkspaceId);
        Assert.Equal(ws2Id, info2.WorkspaceId);
        // Same display name, different ids — name carried no authority.
        Assert.Equal(info1.WorkspaceName, info2.WorkspaceName);
        Assert.NotEqual(info1.WorkspaceId, info2.WorkspaceId);
    }

    [Fact]
    public async Task User2_cannot_switch_into_user1_same_named_workspace_by_id()
    {
        var ws1Id = IdOf(await ControllerFor(User1Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));
        await ControllerFor(User2Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None);

        // User 2 tries to switch to User 1's workspace id (same name) → 403, no membership.
        var result = await ControllerFor(User2Id).Switch(ws1Id, CancellationToken.None);
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);

        // User 2's selection was not mutated to User 1's workspace.
        var u2 = await _db.AppUsers.FirstAsync(u => u.Id == User2Id);
        Assert.NotEqual(ws1Id, u2.CurrentWorkspaceId);
    }

    [Fact]
    public async Task User2_pointing_at_user1_same_named_workspace_is_denied_not_resolved()
    {
        var ws1Id = IdOf(await ControllerFor(User1Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));
        await ControllerFor(User2Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None);

        // Simulate a stale/forged selection: User 2's CurrentWorkspaceId = User 1's ws.
        var u2 = await _db.AppUsers.FirstAsync(u => u.Id == User2Id);
        u2.CurrentWorkspaceId = ws1Id;
        await _db.SaveChangesAsync();

        // Resolver must DENY (403) on membership — the matching name must NOT grant access,
        // and it must NOT fall back to User 2's own workspace.
        await Assert.ThrowsAsync<WorkspaceAccessDeniedException>(
            () => ResolverFor(User2Id).GetCurrentWorkspaceAsync());
    }

    // ── Posts isolation under identical names (G3 data scope) ─────────────────────

    [Fact]
    public async Task Posts_are_isolated_between_same_named_workspaces()
    {
        var ws1Id = IdOf(await ControllerFor(User1Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));
        var ws2Id = IdOf(await ControllerFor(User2Id)
            .Create(new CreateWorkspaceRequest(SharedName), CancellationToken.None));

        var now = DateTime.UtcNow;
        var ws1Post = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = ws1Id, Content = "ws1", Platform = Platform.Facebook,
            ScheduledAt = now.AddHours(1), Status = PostStatus.Scheduled, CreatedAt = now, UpdatedAt = now,
        };
        _db.Posts.Add(ws1Post);
        await _db.SaveChangesAsync();

        // User 2 (workspace resolved by ID, not by the identical name) requests User 1's
        // post by id → 404. The matching workspace NAME grants no access; the single-post
        // lookup is purely `Id == id && WorkspaceId == ws2Id`.
        var workspaceMock = new Mock<ICurrentWorkspaceProvider>();
        workspaceMock.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ws2Id);
        var posts = new PostsController(
            _db, new Mock<IPostScheduler>().Object, new Mock<IFacebookInsightsService>().Object,
            workspaceMock.Object, new PassThroughMediaGate(), NullLogger<PostsController>.Instance);

        var result = await posts.GetPost(ws1Post.Id);
        Assert.IsType<NotFoundResult>(result.Result);

        // The row itself was never moved or shared — still belongs to workspace 1.
        Assert.Equal(ws1Id, (await _db.Posts.AsNoTracking().FirstAsync(p => p.Id == ws1Post.Id)).WorkspaceId);
    }
}
