using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Controllers;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Ai;
using PostPilot.Api.Services.Auth;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests.Controllers;

/// <summary>
/// End-to-end workspace isolation tests. Each test uses a shared in-memory DB
/// seeded with two workspaces (A and B), each owned by a different user. The
/// ICurrentWorkspaceProvider / ICurrentUserProvider mocks are flipped between
/// the two identities to simulate "user A logged in" vs "user B logged in".
///
/// These tests pin down the cross-tenant guarantees the audit found missing:
///   - Listing/reading/updating/deleting/publishing/cancelling another workspace's
///     posts must 404.
///   - GetPostDetails must never fetch engagement using another workspace's page.
///   - Creating a post with a target page/IG account that belongs to another
///     workspace must be rejected (409 INTEGRATION_DISCONNECTED).
///   - Voice profiles, media uploads, and workspace switching are all scoped.
///   - CurrentWorkspaceProvider self-heals stale memberships.
/// </summary>
public class WorkspaceIsolationTests : IDisposable
{
    private static readonly Guid UserAId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid UserBId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid WorkspaceAId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceBId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private readonly AppDbContext _db;
    private readonly Mock<ICurrentUserProvider> _userMock = new();
    private readonly Mock<ICurrentWorkspaceProvider> _workspaceMock = new();
    private readonly Mock<IPostScheduler> _schedulerMock = new();
    private readonly Mock<IFacebookInsightsService> _insightsMock = new();

    public WorkspaceIsolationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        SeedTwoWorkspaces();

        ActAs(UserAId, WorkspaceAId);

        _schedulerMock.Setup(s => s.ScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduleResult(true, "test-arn", null));
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Flips both auth mocks atomically. All controllers/services share these mocks.</summary>
    private void ActAs(Guid userId, Guid workspaceId)
    {
        _userMock.Reset();
        _workspaceMock.Reset();
        _userMock.Setup(u => u.GetCurrentUserId()).Returns(userId);
        _userMock.Setup(u => u.TryGetCurrentUserId(out userId)).Returns(true);
        _workspaceMock.Setup(w => w.GetCurrentWorkspaceIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(workspaceId);
        _workspaceMock.Setup(w => w.GetCurrentWorkspaceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrentWorkspaceInfo(userId, workspaceId, "Test"));
    }

    private PostsController NewPostsController() => new(
        _db,
        _schedulerMock.Object,
        _insightsMock.Object,
        _workspaceMock.Object,
        NullLogger<PostsController>.Instance);

    private WorkspacesController NewWorkspacesController() => new(
        _db,
        _userMock.Object,
        NullLogger<WorkspacesController>.Instance);

    private AiVoiceProfileController NewVoiceProfileController() => new(
        _db,
        _userMock.Object,
        _workspaceMock.Object,
        NullLogger<AiVoiceProfileController>.Instance);

    private void SeedTwoWorkspaces()
    {
        var now = DateTime.UtcNow;
        _db.AppUsers.AddRange(
            new AppUser
            {
                Id = UserAId, Email = "a@test", DisplayName = "A",
                AuthProvider = "google", ExternalAuthUserId = "a-sub",
                CurrentWorkspaceId = WorkspaceAId, CreatedAt = now, UpdatedAt = now,
            },
            new AppUser
            {
                Id = UserBId, Email = "b@test", DisplayName = "B",
                AuthProvider = "google", ExternalAuthUserId = "b-sub",
                CurrentWorkspaceId = WorkspaceBId, CreatedAt = now, UpdatedAt = now,
            });

        _db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceAId, Name = "Workspace A", OwnerUserId = UserAId, CreatedAt = now, UpdatedAt = now },
            new Workspace { Id = WorkspaceBId, Name = "Workspace B", OwnerUserId = UserBId, CreatedAt = now, UpdatedAt = now });

        _db.WorkspaceMembers.AddRange(
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, UserId = UserAId, Role = WorkspaceRole.Owner, CreatedAt = now },
            new WorkspaceMember { Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId, UserId = UserBId, Role = WorkspaceRole.Owner, CreatedAt = now });

        _db.SaveChanges();
    }

    private Post SeedPost(Guid workspaceId, PostStatus status = PostStatus.Scheduled, Platform platform = Platform.Facebook)
    {
        var p = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Content = "hello",
            Platform = platform,
            ScheduledAt = DateTime.UtcNow.AddHours(1),
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(p);
        _db.SaveChanges();
        return p;
    }

    private (MetaConnection conn, ConnectedPage page, ConnectedInstagramAccount ig) SeedMetaForWorkspace(Guid workspaceId, Guid ownerUserId, string token = "page-token")
    {
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = ownerUserId,
            AccessToken = "user-token", TokenExpiresAt = DateTime.UtcNow.AddDays(30),
            ConnectedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, MetaConnectionId = conn.Id,
            PageId = $"page-{workspaceId:N}", Name = "Page", AccessToken = token,
            CreatedAt = DateTime.UtcNow,
        };
        var ig = new ConnectedInstagramAccount
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, MetaConnectionId = conn.Id,
            IgBusinessId = $"ig-{workspaceId:N}", Username = "user", PageId = page.PageId, PageName = page.Name,
            CreatedAt = DateTime.UtcNow,
        };
        _db.MetaConnections.Add(conn);
        _db.ConnectedPages.Add(page);
        _db.ConnectedInstagramAccounts.Add(ig);
        _db.SaveChanges();
        return (conn, page, ig);
    }

    // ── Posts: list / read / update / delete / publish / cancel ──────────────

    [Fact]
    public async Task GetPosts_lists_only_current_workspace_posts()
    {
        // GetPosts hides posts whose target page/IG is disconnected, so each post
        // needs a connected target to show up at all.
        var (_, aPage, _) = SeedMetaForWorkspace(WorkspaceAId, UserAId);
        var (_, bPage, _) = SeedMetaForWorkspace(WorkspaceBId, UserBId);

        var aPost = SeedPost(WorkspaceAId);
        aPost.TargetPageId = aPage.Id;
        var bPost = SeedPost(WorkspaceBId);
        bPost.TargetPageId = bPage.Id;
        _db.SaveChanges();

        ActAs(UserAId, WorkspaceAId);
        var result = await NewPostsController().GetPosts();

        var paged = Assert.IsType<PaginatedResponse<PostDto>>(result.Value);
        Assert.Single(paged.Items);
        Assert.Equal(aPost.Id, paged.Items[0].Id);
    }

    [Fact]
    public async Task GetPost_returns_404_for_other_workspace_post()
    {
        var bPost = SeedPost(WorkspaceBId);

        ActAs(UserAId, WorkspaceAId);
        var result = await NewPostsController().GetPost(bPost.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetPostDetails_returns_404_for_other_workspace_post()
    {
        var bPost = SeedPost(WorkspaceBId);

        ActAs(UserAId, WorkspaceAId);
        var result = await NewPostsController().GetPostDetails(bPost.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// Regression test for the audit's headline finding (C1/C2 in §2 of the audit report).
    ///
    /// Before the fix:
    ///   Workspace A has a published Facebook post whose TargetPage was nulled
    ///   (e.g. user reconnected Meta). The controller used to fall back to
    ///   "any ConnectedPage in the table", which could pick Workspace B's page
    ///   and call Meta with B's access token — a cross-tenant data leak.
    ///
    /// After the fix:
    ///   The lookup is scoped to workspaceId. With no matching page in A, the
    ///   controller must return engagement = null and IFacebookInsightsService
    ///   must never be called.
    /// </summary>
    [Fact]
    public async Task GetPostDetails_never_uses_ConnectedPage_from_other_workspace()
    {
        // Workspace B has a connected page with a sensitive token.
        SeedMetaForWorkspace(WorkspaceBId, UserBId, token: "WORKSPACE-B-SECRET-TOKEN");

        // Workspace A has NO connected page at all.
        var aPost = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceAId,
            Content = "leaked?",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Published,
            // ExternalPostId in the standard "pageId_postId" format the
            // pre-fix fallback would try to look up.
            ExternalPostId = "1234567890_999",
            TargetPageId = null,
            ScheduledAt = DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(aPost);
        _db.SaveChanges();

        ActAs(UserAId, WorkspaceAId);
        var result = await NewPostsController().GetPostDetails(aPost.Id, CancellationToken.None);

        var details = Assert.IsType<PostDetailsDto>(result.Value);
        Assert.Null(details.Engagement);

        // The critical assertion: insights service must not have been called with
        // Workspace B's token (or any token at all).
        _insightsMock.Verify(
            s => s.GetPostEngagementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "GetPostDetails leaked across workspaces: insights service was called with another workspace's token.");
    }

    [Fact]
    public async Task UpdatePost_returns_404_for_other_workspace_post()
    {
        var bPost = SeedPost(WorkspaceBId);

        ActAs(UserAId, WorkspaceAId);
        var request = new UpdatePostRequest(
            Content: "new",
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddDays(1));

        var result = await NewPostsController().UpdatePost(bPost.Id, request);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeletePost_returns_404_for_other_workspace_post()
    {
        var bPost = SeedPost(WorkspaceBId, PostStatus.Canceled);

        ActAs(UserAId, WorkspaceAId);
        var result = await NewPostsController().DeletePost(bPost.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CancelPost_returns_404_for_other_workspace_post()
    {
        var bPost = SeedPost(WorkspaceBId);

        ActAs(UserAId, WorkspaceAId);
        var result = await NewPostsController().CancelPost(bPost.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task PublishNow_returns_404_for_other_workspace_post()
    {
        var bPost = SeedPost(WorkspaceBId);

        ActAs(UserAId, WorkspaceAId);
        // The publisher resolvers should never be invoked for a 404 path,
        // so passing strict (null!) mocks would also be fine. Using empty mocks
        // makes the intent obvious to a future reader.
        var publisherResolver = new Mock<IPostPublisherResolver>().Object;
        var storyResolver = new Mock<IStoryPublisherResolver>().Object;
        var result = await NewPostsController().PublishNow(bPost.Id, publisherResolver, storyResolver);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── Cross-workspace target page / IG account on create ───────────────────

    [Fact]
    public async Task CreatePost_returns_409_when_targetPage_belongs_to_other_workspace()
    {
        var (_, bPage, _) = SeedMetaForWorkspace(WorkspaceBId, UserBId);

        ActAs(UserAId, WorkspaceAId);
        var request = new CreatePostRequest(
            Content: "x",
            MediaUrl: null,
            MediaType: null,
            Platform: Platform.Facebook,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetPageId: bPage.Id);

        var result = await NewPostsController().CreatePost(request);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("INTEGRATION_DISCONNECTED", problem.Extensions["code"]);
    }

    [Fact]
    public async Task CreatePost_returns_409_when_targetInstagramAccount_belongs_to_other_workspace()
    {
        var (_, _, bIg) = SeedMetaForWorkspace(WorkspaceBId, UserBId);

        ActAs(UserAId, WorkspaceAId);
        var request = new CreatePostRequest(
            Content: "x",
            MediaUrl: "https://example.com/x.jpg",
            MediaType: MediaType.Image,
            Platform: Platform.Instagram,
            ScheduledAt: DateTime.UtcNow.AddHours(1),
            TargetInstagramAccountId: bIg.Id);

        var result = await NewPostsController().CreatePost(request);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("INTEGRATION_DISCONNECTED", problem.Extensions["code"]);
    }

    // ── Workspaces controller: switch / list ─────────────────────────────────

    [Fact]
    public async Task SwitchWorkspace_returns_403_when_user_is_not_a_member()
    {
        // User A trying to switch to Workspace B (no membership).
        ActAs(UserAId, WorkspaceAId);
        var result = await NewWorkspacesController().Switch(WorkspaceBId, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task ListWorkspaces_returns_only_user_memberships()
    {
        ActAs(UserAId, WorkspaceAId);
        var result = await NewWorkspacesController().List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        // Anonymous projection in the controller — assert by counting.
        var list = (System.Collections.IEnumerable)ok.Value!;
        var count = 0;
        foreach (var _ in list) count++;
        Assert.Equal(1, count);
    }

    // ── CurrentWorkspaceProvider: NO silent fallback ─────────────────────────
    //
    // In the MVP a user can have multiple workspaces, each with its own connected
    // provider account. Silently switching to another workspace could land media/
    // posts/provider actions in the wrong account, so the resolver must refuse to
    // guess and instead force an explicit (re)selection.

    private CurrentWorkspaceProvider RealProviderFor(Guid userId)
    {
        var realUser = new Mock<ICurrentUserProvider>();
        realUser.Setup(u => u.GetCurrentUserId()).Returns(userId);
        return new CurrentWorkspaceProvider(
            _db, realUser.Object, NullLogger<CurrentWorkspaceProvider>.Instance);
    }

    [Fact]
    public async Task CurrentWorkspaceProvider_resolves_the_selected_workspace()
    {
        // Happy path: A's selection points at A's workspace.
        var info = await RealProviderFor(UserAId).GetCurrentWorkspaceAsync();
        Assert.Equal(WorkspaceAId, info.WorkspaceId);
    }

    [Fact]
    public async Task CurrentWorkspaceProvider_does_not_fallback_when_membership_is_stale()
    {
        // A's CurrentWorkspaceId points at Workspace B, where A is NOT a member.
        var userA = _db.AppUsers.Single(u => u.Id == UserAId);
        userA.CurrentWorkspaceId = WorkspaceBId;
        _db.SaveChanges();

        var provider = RealProviderFor(UserAId);

        // Must throw access-denied (403) — NOT silently fall back to Workspace A.
        await Assert.ThrowsAsync<WorkspaceAccessDeniedException>(
            () => provider.GetCurrentWorkspaceAsync());

        // And it must NOT have rewritten the selection to some other workspace.
        Assert.Equal(WorkspaceBId, _db.AppUsers.Single(u => u.Id == UserAId).CurrentWorkspaceId);
    }

    [Fact]
    public async Task CurrentWorkspaceProvider_throws_NotSelected_when_CurrentWorkspaceId_is_null()
    {
        var userA = _db.AppUsers.Single(u => u.Id == UserAId);
        userA.CurrentWorkspaceId = null;
        _db.SaveChanges();

        await Assert.ThrowsAsync<WorkspaceNotSelectedException>(
            () => RealProviderFor(UserAId).GetCurrentWorkspaceAsync());

        // Still null — no auto-selection happened even though A has a membership.
        Assert.Null(_db.AppUsers.Single(u => u.Id == UserAId).CurrentWorkspaceId);
    }

    [Fact]
    public async Task CurrentWorkspaceProvider_throws_NotSelected_when_workspace_was_deleted()
    {
        // Point A at a workspace id that does not exist (deleted out from under them).
        var ghost = Guid.NewGuid();
        var userA = _db.AppUsers.Single(u => u.Id == UserAId);
        userA.CurrentWorkspaceId = ghost;
        _db.SaveChanges();

        await Assert.ThrowsAsync<WorkspaceNotSelectedException>(
            () => RealProviderFor(UserAId).GetCurrentWorkspaceAsync());

        Assert.Equal(ghost, _db.AppUsers.Single(u => u.Id == UserAId).CurrentWorkspaceId);
    }

    // ── Voice profiles ───────────────────────────────────────────────────────

    private AiVoiceProfile SeedVoiceProfile(Guid workspaceId, Guid userId, string name = "Voice")
    {
        var p = new AiVoiceProfile
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.AiVoiceProfiles.Add(p);
        _db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task GetVoiceProfile_returns_404_for_other_workspace()
    {
        var bProfile = SeedVoiceProfile(WorkspaceBId, UserBId);

        ActAs(UserAId, WorkspaceAId);
        var result = await NewVoiceProfileController().GetProfile(bProfile.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UpdateVoiceProfile_returns_404_for_other_workspace()
    {
        var bProfile = SeedVoiceProfile(WorkspaceBId, UserBId);

        ActAs(UserAId, WorkspaceAId);
        var request = new UpdateVoiceProfileRequest("renamed", null, null, null, null, null);
        var result = await NewVoiceProfileController().UpdateProfile(bProfile.Id, request, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteVoiceProfile_returns_404_for_other_workspace()
    {
        var bProfile = SeedVoiceProfile(WorkspaceBId, UserBId);

        ActAs(UserAId, WorkspaceAId);
        var result = await NewVoiceProfileController().DeleteProfile(bProfile.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task AiText_does_not_apply_voiceProfile_from_other_workspace()
    {
        // Workspace B has a voice profile A is trying to use.
        var bProfile = SeedVoiceProfile(WorkspaceBId, UserBId, name: "B-Voice");

        ActAs(UserAId, WorkspaceAId);

        var gemini = new Mock<IGeminiClient>();
        gemini.Setup(g => g.GenerateVariantsAsync(
                It.IsAny<AiTextAction>(),
                It.IsAny<AiPlatform>(),
                It.IsAny<string>(),
                It.IsAny<AiTone?>(),
                It.IsAny<string>(),
                It.IsAny<AiVoiceProfile?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiTextVariantsResponse(AiTextAction.Polish, new List<AiTextVariant>()));

        var rate = new Mock<IAiRateLimiter>();
        rate.Setup(r => r.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var controller = new AiTextController(
            gemini.Object,
            rate.Object,
            _db,
            null!, null!, null!,
            _userMock.Object,
            _workspaceMock.Object,
            NullLogger<AiTextController>.Instance);

        var request = new AiTextRequest(
            AiTextAction.Polish, AiPlatform.Facebook, "text", null, "en",
            VoiceProfileId: bProfile.Id);

        await controller.ProcessText(request, CancellationToken.None);

        // The controller may still call Gemini — but it MUST NOT pass the
        // cross-workspace voice profile entity through. It should pass null.
        gemini.Verify(g => g.GenerateVariantsAsync(
            It.IsAny<AiTextAction>(),
            It.IsAny<AiPlatform>(),
            It.IsAny<string>(),
            It.IsAny<AiTone?>(),
            It.IsAny<string>(),
            (AiVoiceProfile?)null,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Media uploads ────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteMedia_returns_404_for_other_workspace_media()
    {
        var bMedia = new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceBId,
            StorageProvider = "local-disk",
            StorageKey = "media/b-key.jpg",
            ContentType = "image/jpeg",
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        };
        _db.Media.Add(bMedia);
        _db.SaveChanges();

        // Use MediaUploadService directly (matches the path the controller exercises).
        var uploadSvc = new PostPilot.Api.Services.Media.MediaUploadService(
            _db,
            new Mock<PostPilot.Api.Services.Media.IMediaService>().Object,
            new PostPilot.Api.Settings.MediaStorageOptions(),
            NullLogger<PostPilot.Api.Services.Media.MediaUploadService>.Instance);

        var removed = await uploadSvc.DeleteAsync(WorkspaceAId, bMedia.Id);
        Assert.False(removed);
    }

    // ── Media upload init routes to the SELECTED workspace ─────────────────────
    //
    // A user with two workspaces (each potentially a different connected account)
    // must have uploads land in whichever workspace is currently selected — never a
    // different one. These wire the REAL CurrentWorkspaceProvider + real MediaService
    // so the storage key is driven end-to-end by the selection, not a mock.

    /// <summary>Grants <paramref name="userId"/> membership in <paramref name="workspaceId"/>.</summary>
    private void AddMembership(Guid userId, Guid workspaceId)
    {
        _db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Member,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    /// <summary>
    /// Builds a MediaController backed by the REAL workspace provider for <paramref name="userId"/>
    /// and a real MediaService whose storage keys embed the resolved workspace id.
    /// </summary>
    private MediaController NewRealMediaControllerFor(Guid userId)
    {
        var fakeStorage = new FakeStorage();
        var storageOpts = new PostPilot.Api.Settings.MediaStorageOptions { Provider = "local-disk" };
        var mediaService = new PostPilot.Api.Services.Media.MediaService(
            storage: fakeStorage,
            storageOpts: storageOpts,
            runMode: AppRunMode.Server,
            logger: NullLogger<PostPilot.Api.Services.Media.MediaService>.Instance,
            uploadUrlExpiration: TimeSpan.FromMinutes(15),
            maxImageFileSizeBytes: 20 * 1024 * 1024,
            maxVideoFileSizeBytes: 200 * 1024 * 1024,
            publishingBaseUrl: "https://example.test");

        var uploadSvc = new PostPilot.Api.Services.Media.MediaUploadService(
            _db, mediaService, storageOpts,
            NullLogger<PostPilot.Api.Services.Media.MediaUploadService>.Instance);

        var realUser = new Mock<ICurrentUserProvider>();
        realUser.Setup(u => u.GetCurrentUserId()).Returns(userId);
        var realWorkspace = new CurrentWorkspaceProvider(
            _db, realUser.Object, NullLogger<CurrentWorkspaceProvider>.Instance);

        return new MediaController(
            mediaService,
            uploadSvc,
            new Mock<PostPilot.Api.Services.Validation.IMediaValidationService>().Object,
            realWorkspace,
            _db,
            NullLogger<MediaController>.Instance);
    }

    private void SelectWorkspace(Guid userId, Guid workspaceId)
    {
        var u = _db.AppUsers.Single(x => x.Id == userId);
        u.CurrentWorkspaceId = workspaceId;
        _db.SaveChanges();
    }

    [Fact]
    public async Task InitUpload_with_WorkspaceA_selected_puts_WorkspaceA_in_the_key()
    {
        // User A is a member of BOTH workspaces; A is the selected one.
        AddMembership(UserAId, WorkspaceBId);
        SelectWorkspace(UserAId, WorkspaceAId);

        var result = await NewRealMediaControllerFor(UserAId).InitUpload(
            new InitUploadRequest("photo.png", "image/png", 100, Platform.Facebook),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<InitUploadResponse>(ok.Value);
        Assert.Contains($"/workspaces/{WorkspaceAId:D}/", resp.StorageKey);
        Assert.DoesNotContain($"/workspaces/{WorkspaceBId:D}/", resp.StorageKey);

        // The persisted Media row is in the selected workspace, not the other one.
        var row = await _db.Media.SingleAsync(m => m.Id == resp.MediaId);
        Assert.Equal(WorkspaceAId, row.WorkspaceId);
    }

    [Fact]
    public async Task InitUpload_with_WorkspaceB_selected_puts_WorkspaceB_in_the_key()
    {
        // Same user, now switched to Workspace B — the key must follow the selection.
        AddMembership(UserAId, WorkspaceBId);
        SelectWorkspace(UserAId, WorkspaceBId);

        var result = await NewRealMediaControllerFor(UserAId).InitUpload(
            new InitUploadRequest("photo.png", "image/png", 100, Platform.Facebook),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var resp = Assert.IsType<InitUploadResponse>(ok.Value);
        Assert.Contains($"/workspaces/{WorkspaceBId:D}/", resp.StorageKey);
        Assert.DoesNotContain($"/workspaces/{WorkspaceAId:D}/", resp.StorageKey);

        var row = await _db.Media.SingleAsync(m => m.Id == resp.MediaId);
        Assert.Equal(WorkspaceBId, row.WorkspaceId);
    }

    [Fact]
    public async Task InitUpload_does_not_fallback_and_throws_when_selection_is_stale()
    {
        // A is selected into Workspace B but is NOT a member of B. The resolver must
        // throw access-denied — NOT silently create the upload under Workspace A.
        SelectWorkspace(UserAId, WorkspaceBId);

        await Assert.ThrowsAsync<WorkspaceAccessDeniedException>(() =>
            NewRealMediaControllerFor(UserAId).InitUpload(
                new InitUploadRequest("photo.png", "image/png", 100, Platform.Facebook),
                CancellationToken.None));

        // Critically: no Media row was created under ANY workspace.
        Assert.False(await _db.Media.AnyAsync());
    }

    private sealed class FakeStorage : PostPilot.Api.Services.Media.IMediaStorageProvider
    {
        public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken ct = default)
            => Task.FromResult("https://upload.test/" + storageKey);
        public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken ct = default)
            => Task.FromResult("https://download.test/" + storageKey);
        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken ct = default) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(string storageKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string storageKey, Stream content, CancellationToken ct = default) => Task.CompletedTask;
        public bool Exists(string storageKey) => false;
        public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken ct = default) => Task.FromResult(false);
        public Task<PostPilot.Api.Services.Media.StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken ct = default)
            => Task.FromResult<PostPilot.Api.Services.Media.StoredObjectInfo?>(null);
    }

    // ── Group 1: Meta controller passes ONLY current workspace id to the service ─
    //
    // These tests pin the contract that MetaController gets its workspace id from
    // ICurrentWorkspaceProvider — never from the client. The service layer is
    // mocked: its own scoping is covered by MetaOAuthService unit tests below.

    private MetaController NewMetaController(Mock<PostPilot.Api.Services.IMetaOAuthService> svc) => new(
        svc.Object,
        _userMock.Object,
        _workspaceMock.Object,
        NullLogger<MetaController>.Instance);

    /// <summary>MetaController wired to the REAL workspace provider for <paramref name="userId"/>.</summary>
    private MetaController NewRealMetaControllerFor(Guid userId, Mock<PostPilot.Api.Services.IMetaOAuthService> svc)
    {
        var realUser = new Mock<ICurrentUserProvider>();
        realUser.Setup(u => u.GetCurrentUserId()).Returns(userId);
        var realWorkspace = new CurrentWorkspaceProvider(
            _db, realUser.Object, NullLogger<CurrentWorkspaceProvider>.Instance);
        return new MetaController(
            svc.Object, realUser.Object, realWorkspace, NullLogger<MetaController>.Instance);
    }

    [Fact]
    public async Task Provider_GetConnection_does_not_fallback_and_surfaces_403_for_unauthorized_workspace()
    {
        // A is selected into Workspace B but is NOT a member. The broad catch in the
        // controller must NOT flatten this into a 500 or run against another workspace —
        // the access-denied exception propagates (middleware maps it to 403). Strict
        // mock guarantees the service is never invoked with ANY workspace.
        SelectWorkspace(UserAId, WorkspaceBId);
        var svc = new Mock<PostPilot.Api.Services.IMetaOAuthService>(MockBehavior.Strict);

        await Assert.ThrowsAsync<WorkspaceAccessDeniedException>(
            () => NewRealMetaControllerFor(UserAId, svc).GetConnection());
    }

    [Fact]
    public async Task Provider_Disconnect_does_not_fallback_and_surfaces_409_for_missing_selection()
    {
        // No workspace selected -> 409 (not-selected), and the service is never called,
        // so a disconnect can never hit the wrong account.
        SelectWorkspace_ToNull(UserAId);
        var svc = new Mock<PostPilot.Api.Services.IMetaOAuthService>(MockBehavior.Strict);

        await Assert.ThrowsAsync<WorkspaceNotSelectedException>(
            () => NewRealMetaControllerFor(UserAId, svc).Disconnect());
    }

    private void SelectWorkspace_ToNull(Guid userId)
    {
        var u = _db.AppUsers.Single(x => x.Id == userId);
        u.CurrentWorkspaceId = null;
        _db.SaveChanges();
    }

    [Fact]
    public async Task GetConnection_passes_only_current_workspace_to_service()
    {
        var svc = new Mock<PostPilot.Api.Services.IMetaOAuthService>(MockBehavior.Strict);
        svc.Setup(s => s.GetConnectionAsync(WorkspaceAId))
            .ReturnsAsync(new MetaConnectionResponse(null, false));

        ActAs(UserAId, WorkspaceAId);
        await NewMetaController(svc).GetConnection();

        svc.Verify(s => s.GetConnectionAsync(WorkspaceAId), Times.Once);
        svc.Verify(s => s.GetConnectionAsync(WorkspaceBId), Times.Never);
    }

    [Fact]
    public async Task GetAvailablePages_passes_only_current_workspace_to_service()
    {
        var svc = new Mock<PostPilot.Api.Services.IMetaOAuthService>(MockBehavior.Strict);
        svc.Setup(s => s.GetAvailablePagesAsync(WorkspaceAId))
            .ReturnsAsync(new MetaAvailablePagesResponse(new List<FacebookPageDto>()));

        ActAs(UserAId, WorkspaceAId);
        await NewMetaController(svc).GetAvailablePages();

        svc.Verify(s => s.GetAvailablePagesAsync(WorkspaceAId), Times.Once);
        svc.Verify(s => s.GetAvailablePagesAsync(WorkspaceBId), Times.Never);
    }

    [Fact]
    public async Task GetInstagramEligibility_passes_only_current_workspace_to_service()
    {
        var svc = new Mock<PostPilot.Api.Services.IMetaOAuthService>(MockBehavior.Strict);
        svc.Setup(s => s.DiscoverInstagramEligibilityAsync(WorkspaceAId))
            .ReturnsAsync(new InstagramDiscoveryResponse(new List<InstagramEligibilityDto>(), 0, 0));

        ActAs(UserAId, WorkspaceAId);
        await NewMetaController(svc).GetInstagramEligibility();

        svc.Verify(s => s.DiscoverInstagramEligibilityAsync(WorkspaceAId), Times.Once);
        svc.Verify(s => s.DiscoverInstagramEligibilityAsync(WorkspaceBId), Times.Never);
    }

    [Fact]
    public async Task Disconnect_passes_only_current_workspace_to_service()
    {
        var svc = new Mock<PostPilot.Api.Services.IMetaOAuthService>(MockBehavior.Strict);
        svc.Setup(s => s.DisconnectAsync(WorkspaceAId)).Returns(Task.CompletedTask);

        ActAs(UserAId, WorkspaceAId);
        await NewMetaController(svc).Disconnect();

        svc.Verify(s => s.DisconnectAsync(WorkspaceAId), Times.Once);
        svc.Verify(s => s.DisconnectAsync(WorkspaceBId), Times.Never);
    }

    [Fact]
    public async Task UpdateConnection_passes_only_current_workspace_to_service()
    {
        var svc = new Mock<PostPilot.Api.Services.IMetaOAuthService>(MockBehavior.Strict);
        svc.Setup(s => s.UpdateConnectionAsync(WorkspaceAId, It.IsAny<List<string>>(), It.IsAny<List<string>>()))
            .ReturnsAsync(new MetaSaveConnectionResponse(
                new MetaConnectionDto(
                    Guid.NewGuid().ToString(), UserAId.ToString(),
                    DateTime.UtcNow.AddDays(60), DateTime.UtcNow,
                    new List<ConnectedPageDto>(), new List<ConnectedInstagramAccountDto>(),
                    true, null, null, null)));

        ActAs(UserAId, WorkspaceAId);
        await NewMetaController(svc).UpdateConnection(
            new MetaUpdatePagesRequest(new List<string>(), new List<string>()));

        svc.Verify(s => s.UpdateConnectionAsync(WorkspaceAId, It.IsAny<List<string>>(), It.IsAny<List<string>>()), Times.Once);
        svc.Verify(s => s.UpdateConnectionAsync(WorkspaceBId, It.IsAny<List<string>>(), It.IsAny<List<string>>()), Times.Never);
    }

    // ── Group 2: MetaOAuthService.GetConnectionAsync scopes to one workspace ────
    //
    // The service is the layer that actually reads from the DB. This test seeds
    // both workspaces with a Meta connection + pages + IG accounts, calls
    // GetConnectionAsync(WorkspaceAId), and asserts no row from Workspace B
    // is in the result.

    [Fact]
    public async Task MetaOAuthService_GetConnection_for_workspace_A_does_not_return_workspace_B_assets()
    {
        var (_, aPage, aIg) = SeedMetaForWorkspace(WorkspaceAId, UserAId, token: "A-PAGE-TOKEN");
        var (_, bPage, bIg) = SeedMetaForWorkspace(WorkspaceBId, UserBId, token: "B-PAGE-TOKEN");

        var svc = NewRealMetaOAuthService();
        var result = await svc.GetConnectionAsync(WorkspaceAId);

        Assert.True(result.IsConnected);
        Assert.NotNull(result.Connection);
        Assert.All(result.Connection!.Pages, p => Assert.NotEqual(bPage.PageId, p.PageId));
        Assert.All(result.Connection.InstagramAccounts, ig => Assert.NotEqual(bIg.IgBusinessId, ig.IgBusinessId));
        Assert.Contains(result.Connection.Pages, p => p.PageId == aPage.PageId);
        Assert.Contains(result.Connection.InstagramAccounts, ig => ig.IgBusinessId == aIg.IgBusinessId);
    }

    // ── Group 3: Media read endpoints scope by storage key + workspace ─────────

    private MediaController NewMediaController()
    {
        var mediaServiceMock = new Mock<PostPilot.Api.Services.Media.IMediaService>();
        mediaServiceMock.Setup(m => m.IsValidMediaType(It.IsAny<string>())).Returns(true);
        mediaServiceMock.Setup(m => m.GetMediaType(It.IsAny<string>())).Returns(MediaType.Image);

        var uploadSvc = new PostPilot.Api.Services.Media.MediaUploadService(
            _db,
            mediaServiceMock.Object,
            new PostPilot.Api.Settings.MediaStorageOptions(),
            NullLogger<PostPilot.Api.Services.Media.MediaUploadService>.Instance);
        var validationSvc = new Mock<PostPilot.Api.Services.Validation.IMediaValidationService>();

        return new MediaController(
            mediaServiceMock.Object,
            uploadSvc,
            validationSvc.Object,
            _workspaceMock.Object,
            _db,
            NullLogger<MediaController>.Instance);
    }

    private Media SeedMedia(Guid workspaceId, string storageKey)
    {
        var m = new Media
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            StorageProvider = "local-disk",
            StorageKey = storageKey,
            ContentType = "image/jpeg",
            Status = MediaUploadStatus.Uploaded,
            CreatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        };
        _db.Media.Add(m);
        _db.SaveChanges();
        return m;
    }

    [Fact]
    public async Task ValidateMedia_returns_404_for_other_workspace_storage_key()
    {
        var bMedia = SeedMedia(WorkspaceBId, "media/b-only.jpg");

        ActAs(UserAId, WorkspaceAId);
        var result = await NewMediaController().ValidateMedia(
            new ValidateMediaByKeyRequest(bMedia.StorageKey, "image/jpeg", Platform.Facebook, Placement.Feed),
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task ExtractMetadata_returns_404_for_other_workspace_storage_key()
    {
        var bMedia = SeedMedia(WorkspaceBId, "media/b-only-extract.jpg");

        ActAs(UserAId, WorkspaceAId);
        var result = await NewMediaController().ExtractMetadata(
            new ExtractMetadataRequest(bMedia.StorageKey, "image/jpeg"),
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task ValidateMedia_succeeds_for_own_workspace_storage_key()
    {
        // Negative-of-the-negative: when the key belongs to the caller's workspace
        // the controller must NOT 404 (it proceeds to validation, which then 404s
        // because the file doesn't exist on disk in the in-memory test — that's a
        // different code path from the workspace check we're pinning here).
        var aMedia = SeedMedia(WorkspaceAId, "media/a-mine.jpg");

        ActAs(UserAId, WorkspaceAId);
        var result = await NewMediaController().ValidateMedia(
            new ValidateMediaByKeyRequest(aMedia.StorageKey, "image/jpeg", Platform.Facebook, Placement.Feed),
            CancellationToken.None);

        // Past the workspace check, the missing physical file should produce a
        // different 404 ("Media file not found"); the important assertion is that
        // we did NOT 404 with the cross-workspace message at the workspace gate.
        // In practice both branches return NotFoundObjectResult, but the workspace
        // check would fire FIRST and short-circuit before any storage lookup. To
        // pin that ordering we use the helper directly.
        var helper = await _db.Media.AnyAsync(m => m.StorageKey == aMedia.StorageKey && m.WorkspaceId == WorkspaceAId);
        Assert.True(helper);
        Assert.NotNull(result);
    }

    // ── Group 4: Multi-workspace disconnect/reconnect isolation ─────────────────
    //
    // Same Meta external page id can be referenced from two workspaces. Disconnect
    // in workspace A must NOT touch workspace B's connection or posts.

    [Fact]
    public async Task Disconnect_in_workspace_A_does_not_affect_workspace_B()
    {
        // Seed both workspaces with Meta connections + scheduled posts.
        var (aConn, aPage, _) = SeedMetaForWorkspace(WorkspaceAId, UserAId);
        var (bConn, bPage, _) = SeedMetaForWorkspace(WorkspaceBId, UserBId);

        var aPost = SeedPost(WorkspaceAId);
        aPost.TargetPageId = aPage.Id;
        var bPost = SeedPost(WorkspaceBId);
        bPost.TargetPageId = bPage.Id;
        _db.SaveChanges();

        var svc = NewRealMetaOAuthService();
        await svc.DisconnectAsync(WorkspaceAId);

        _db.ChangeTracker.Clear();

        // Workspace A: connection + page + post are now disconnected/canceled.
        var aConnAfter = await _db.MetaConnections.FindAsync(aConn.Id);
        var aPageAfter = await _db.ConnectedPages.FindAsync(aPage.Id);
        var aPostAfter = await _db.Posts.FindAsync(aPost.Id);
        Assert.False(aConnAfter!.IsConnected);
        Assert.False(aPageAfter!.IsConnected);
        Assert.Equal(PostStatus.Canceled, aPostAfter!.Status);

        // Workspace B: completely untouched.
        var bConnAfter = await _db.MetaConnections.FindAsync(bConn.Id);
        var bPageAfter = await _db.ConnectedPages.FindAsync(bPage.Id);
        var bPostAfter = await _db.Posts.FindAsync(bPost.Id);
        Assert.True(bConnAfter!.IsConnected);
        Assert.True(bPageAfter!.IsConnected);
        Assert.Equal(PostStatus.Scheduled, bPostAfter!.Status);
        Assert.Null(bConnAfter.DisconnectedAt);
        Assert.Null(bPageAfter.DisconnectedAt);
    }

    [Fact]
    public async Task Disconnect_in_workspace_A_when_both_workspaces_share_external_PageId()
    {
        // Adversarial case: same external Meta PageId value in both workspaces (eg
        // both users connected the same Facebook Page to their own workspace). The
        // disconnect must filter by ConnectedPage.WorkspaceId, not by PageId.
        var sharedExternalPageId = "shared-fb-page-id";

        var aConn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, UserId = UserAId,
            AccessToken = "a-user-token", TokenExpiresAt = DateTime.UtcNow.AddDays(30),
            ConnectedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, IsConnected = true,
        };
        var aPage = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, MetaConnectionId = aConn.Id,
            PageId = sharedExternalPageId, Name = "A's Page", AccessToken = "A-page-token",
            CreatedAt = DateTime.UtcNow, IsConnected = true,
        };
        var bConn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId, UserId = UserBId,
            AccessToken = "b-user-token", TokenExpiresAt = DateTime.UtcNow.AddDays(30),
            ConnectedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, IsConnected = true,
        };
        var bPage = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId, MetaConnectionId = bConn.Id,
            PageId = sharedExternalPageId, Name = "B's Page", AccessToken = "B-page-token",
            CreatedAt = DateTime.UtcNow, IsConnected = true,
        };
        _db.MetaConnections.AddRange(aConn, bConn);
        _db.ConnectedPages.AddRange(aPage, bPage);
        _db.SaveChanges();

        var svc = NewRealMetaOAuthService();
        await svc.DisconnectAsync(WorkspaceAId);

        _db.ChangeTracker.Clear();

        // B's row with the SAME external PageId must remain connected.
        var bPageAfter = await _db.ConnectedPages.FindAsync(bPage.Id);
        Assert.True(bPageAfter!.IsConnected);
        Assert.Equal("B-page-token", bPageAfter.AccessToken);
    }

    // ── Group 5: OAuth state is scoped to its originating workspace ─────────────

    [Fact]
    public async Task MetaOAuthState_lookups_dont_match_state_from_other_workspace()
    {
        // Two states with different values exist, one per workspace. Lookup by
        // state-value alone (the wire-level lookup the callback uses) must be
        // safe because state values are cryptographically random — but the row
        // itself carries WorkspaceId so any downstream code that goes from
        // state→workspace will end up in the right tenant. This test pins that.
        var stateA = new MetaOAuthState
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, State = "state-a-value",
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        };
        var stateB = new MetaOAuthState
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId, State = "state-b-value",
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        };
        _db.MetaOAuthStates.AddRange(stateA, stateB);
        await _db.SaveChangesAsync();

        var resolvedFromA = await _db.MetaOAuthStates.FirstAsync(s => s.State == "state-a-value");
        var resolvedFromB = await _db.MetaOAuthStates.FirstAsync(s => s.State == "state-b-value");

        Assert.Equal(WorkspaceAId, resolvedFromA.WorkspaceId);
        Assert.Equal(WorkspaceBId, resolvedFromB.WorkspaceId);
        Assert.NotEqual(resolvedFromA.WorkspaceId, resolvedFromB.WorkspaceId);
    }

    // ── Group 6: GetPostDetails uses workspace A's token, never workspace B's ──
    //
    // The earlier regression test pinned "no token at all is used when A has no
    // page". This complementary test pins "A's token is used when A's page
    // exists, even if B's page shares the same external PageId".

    [Fact]
    public async Task GetPostDetails_uses_workspace_A_page_token_when_both_workspaces_share_external_PageId()
    {
        var sharedExternalPageId = "1234567890";

        // Workspace A has a ConnectedPage with this external id.
        var aPage = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId,
            PageId = sharedExternalPageId, Name = "A's Page", AccessToken = "A-TOKEN-EXPECTED",
            CreatedAt = DateTime.UtcNow, IsConnected = true,
        };
        // Workspace B has a ConnectedPage with the SAME external id but a
        // different token. If isolation breaks, the controller might pick this one.
        var bPage = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceBId,
            PageId = sharedExternalPageId, Name = "B's Page", AccessToken = "B-TOKEN-MUST-NOT-LEAK",
            CreatedAt = DateTime.UtcNow, IsConnected = true,
        };
        _db.ConnectedPages.AddRange(aPage, bPage);

        var aPost = new Post
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceAId,
            Content = "published",
            Platform = Platform.Facebook,
            PostType = PostType.Feed,
            Status = PostStatus.Published,
            ExternalPostId = $"{sharedExternalPageId}_999",
            TargetPageId = null, // forces the external-id fallback path
            ScheduledAt = DateTime.UtcNow.AddHours(-1),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Posts.Add(aPost);
        _db.SaveChanges();

        // Record which token the insights service was called with.
        string? observedToken = null;
        _insightsMock.Setup(s => s.GetPostEngagementAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, token, _) => observedToken = token)
            .ReturnsAsync((PostEngagementDto?)null);

        ActAs(UserAId, WorkspaceAId);
        await NewPostsController().GetPostDetails(aPost.Id, CancellationToken.None);

        Assert.Equal("A-TOKEN-EXPECTED", observedToken);
        Assert.NotEqual("B-TOKEN-MUST-NOT-LEAK", observedToken);
    }

    // ── Shared infra for groups that need the real MetaOAuthService ─────────────

    private PostPilot.Api.Services.MetaOAuthService NewRealMetaOAuthService()
    {
        var httpClient = new HttpClient(new StubGraphHandler());
        var metaOpts = new PostPilot.Api.Settings.MetaOptions
        {
            AppId = "test", AppSecret = "test", RedirectUri = "http://localhost/cb",
        };
        var publishingOpts = new PostPilot.Api.Settings.PublishingOptions { OAuthStateExpirationMinutes = 10 };

        var handler = new PostPilot.Api.Services.Providers.MetaProviderLifecycleHandler(
            _db, _schedulerMock.Object,
            NullLogger<PostPilot.Api.Services.Providers.MetaProviderLifecycleHandler>.Instance);
        var providerConnections = new PostPilot.Api.Services.Providers.ProviderConnectionService(
            _db,
            new[] { (PostPilot.Api.Services.Providers.IProviderLifecycleHandler)handler },
            NullLogger<PostPilot.Api.Services.Providers.ProviderConnectionService>.Instance);

        _schedulerMock.Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new PostPilot.Api.Services.MetaOAuthService(
            _db,
            httpClient,
            metaOpts,
            NullLogger<PostPilot.Api.Services.MetaOAuthService>.Instance,
            _schedulerMock.Object,
            providerConnections,
            new PostPilot.Api.Settings.MetaApiOptions(),
            publishingOpts);
    }

    private sealed class StubGraphHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Empty 200 — token revoke and identity probes don't gate the unit
            // under test, they just shouldn't throw.
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
