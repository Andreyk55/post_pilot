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

    // ── CurrentWorkspaceProvider: self-heal stale membership ─────────────────

    [Fact]
    public async Task CurrentWorkspaceProvider_repairs_stale_CurrentWorkspaceId()
    {
        // Make User A's CurrentWorkspaceId point at Workspace B, where A is NOT a member.
        var userA = _db.AppUsers.Single(u => u.Id == UserAId);
        userA.CurrentWorkspaceId = WorkspaceBId;
        _db.SaveChanges();

        // Use the real provider here, not the mock — we're testing the provider itself.
        var realUser = new Mock<ICurrentUserProvider>();
        realUser.Setup(u => u.GetCurrentUserId()).Returns(UserAId);

        var provider = new CurrentWorkspaceProvider(
            _db, realUser.Object, NullLogger<CurrentWorkspaceProvider>.Instance);

        var info = await provider.GetCurrentWorkspaceAsync();

        Assert.Equal(WorkspaceAId, info.WorkspaceId);
        Assert.Equal(WorkspaceAId, _db.AppUsers.Single(u => u.Id == UserAId).CurrentWorkspaceId);
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
}
