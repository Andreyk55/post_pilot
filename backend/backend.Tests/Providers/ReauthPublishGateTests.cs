using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Publishing;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Providers;

/// <summary>
/// Pins the publish gate that distinguishes OWNERSHIP (IsConnected) from
/// PUBLISHABILITY (IsConnected AND Status == Active):
///
///   - IsConnected=true  + Status=Active         → publish allowed
///   - IsConnected=true  + Status=ReauthRequired → ownership held, publish BLOCKED
///   - IsConnected=false                         → disconnected, publish BLOCKED
///
/// Covers both gates: the PostPublishingWorker due-posts query (scheduled + retry)
/// and the publisher's own prerequisite check (publish-now / direct call).
/// </summary>
public class ReauthPublishGateTests : IDisposable
{
    private static readonly Guid WorkspaceAId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceBId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private readonly AppDbContext _db;
    private readonly Mock<IPostScheduler> _schedulerMock = new();

    public ReauthPublishGateTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _schedulerMock.Setup(s => s.CancelScheduleAsync(It.IsAny<Post>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (MetaConnection conn, ConnectedPage page) SeedMeta(
        Guid workspaceId, ConnectionStatus status, string pageId = "fb-page-1", string accountId = "meta-alpha")
    {
        var now = DateTime.UtcNow;
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = Guid.NewGuid(),
            Provider = ProviderType.Meta, ProviderAccountId = accountId,
            AccessToken = "user-token", TokenExpiresAt = now.AddDays(30),
            ConnectedAt = now, UpdatedAt = now, IsConnected = true, Status = status,
        };
        var page = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, MetaConnectionId = conn.Id,
            PageId = pageId, Name = "Page", AccessToken = "page-token",
            CreatedAt = now, IsConnected = true, Status = status,
        };
        _db.MetaConnections.Add(conn);
        _db.ConnectedPages.Add(page);
        _db.SaveChanges();
        return (conn, page);
    }

    private Post SeedPost(Guid workspaceId, Guid pageId, PostStatus status, DateTime? due = null)
    {
        var now = DateTime.UtcNow;
        var p = new Post
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Content = "hello",
            Platform = Platform.Facebook, TargetPageId = pageId,
            ScheduledAt = due ?? now.AddMinutes(-5),
            NextRetryAt = status == PostStatus.RetryPending ? (due ?? now.AddMinutes(-5)) : null,
            Status = status, CreatedAt = now, UpdatedAt = now,
        };
        _db.Posts.Add(p);
        _db.SaveChanges();
        return p;
    }

    /// <summary>Replica of PostPublishingWorker.ProcessDuePostsAsync due-posts query (publish gate).</summary>
    private async Task<List<Guid>> QueryDuePostIdsAsync()
    {
        var now = DateTime.UtcNow;
        return await _db.Posts
            .Where(p => p.Platform == Platform.Facebook || p.Platform == Platform.Instagram)
            .Where(p =>
                (p.Platform == Platform.Facebook
                    && p.TargetPage != null
                    && p.TargetPage.IsConnected
                    && p.TargetPage.Status == ConnectionStatus.Active
                    && (p.TargetPage.MetaConnection == null
                        || (p.TargetPage.MetaConnection.IsConnected
                            && p.TargetPage.MetaConnection.Status == ConnectionStatus.Active)))
                || (p.Platform == Platform.Instagram
                    && p.TargetInstagramAccount != null
                    && p.TargetInstagramAccount.IsConnected
                    && p.TargetInstagramAccount.Status == ConnectionStatus.Active
                    && (p.TargetInstagramAccount.MetaConnection == null
                        || (p.TargetInstagramAccount.MetaConnection.IsConnected
                            && p.TargetInstagramAccount.MetaConnection.Status == ConnectionStatus.Active))))
            .Where(p =>
                (p.Status == PostStatus.Scheduled && p.ScheduledAt <= now) ||
                ((p.Status == PostStatus.RetryPending || p.Status == PostStatus.Processing)
                    && p.NextRetryAt != null && p.NextRetryAt <= now))
            .Select(p => p.Id)
            .ToListAsync();
    }

    private FacebookPagePublisher MakePublisher()
    {
        var mediaMock = new Mock<IMediaService>();
        mediaMock.Setup(m => m.IsStorageKey(It.IsAny<string?>()))
            .Returns<string?>(u => u != null && !u.StartsWith("http"));
        var handler = new MetaProviderLifecycleHandler(
            _db, _schedulerMock.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        var providerConnections = new ProviderConnectionService(
            _db, new[] { (IProviderLifecycleHandler)handler }, NullLogger<ProviderConnectionService>.Instance);
        // HttpClient that throws if ever called — proves the gate blocks BEFORE any Meta call.
        var httpClient = new HttpClient(new ThrowingHandler());
        return new FacebookPagePublisher(
            _db, _schedulerMock.Object, mediaMock.Object, new FeatureSettings(),
            httpClient, NullLogger<FacebookPagePublisher>.Instance, providerConnections,
            new MetaApiOptions(),
            new PublishingOptions { MediaDownloadUrlExpirationMinutes = 60, VideoDownloadUrlExpirationMinutes = 120, OAuthStateExpirationMinutes = 10 });
    }

    // ── 1. ReauthRequired blocks publishing (scheduled) ────────────────────────

    [Fact]
    public async Task ReauthRequired_excludes_scheduled_post_from_worker_query()
    {
        var (_, page) = SeedMeta(WorkspaceAId, ConnectionStatus.ReauthRequired);
        var post = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled);

        var due = await QueryDuePostIdsAsync();
        Assert.DoesNotContain(post.Id, due);
    }

    [Fact]
    public async Task ReauthRequired_publisher_gate_blocks_publish_before_any_meta_call()
    {
        var (_, page) = SeedMeta(WorkspaceAId, ConnectionStatus.ReauthRequired);
        var post = SeedPost(WorkspaceAId, page.Id, PostStatus.Scheduled);

        var result = await MakePublisher().PublishAsync(post.Id);

        Assert.False(result.Success);
        Assert.Contains("reauthorized", result.ErrorMessage);

        // The post was NOT claimed/mutated — it stays Scheduled (not stranded in Publishing).
        await _db.Entry(post).ReloadAsync();
        Assert.Equal(PostStatus.Scheduled, post.Status);
    }

    // ── 2. Retry does not publish while ReauthRequired ─────────────────────────

    [Fact]
    public async Task ReauthRequired_excludes_retrypending_post_from_worker_query()
    {
        var (_, page) = SeedMeta(WorkspaceAId, ConnectionStatus.ReauthRequired);
        var post = SeedPost(WorkspaceAId, page.Id, PostStatus.RetryPending);

        var due = await QueryDuePostIdsAsync();
        Assert.DoesNotContain(post.Id, due);
    }

    // ── 3. After reconnect sets Status=Active, retry can publish ───────────────

    [Fact]
    public async Task After_status_set_active_worker_query_includes_post_again()
    {
        var (conn, page) = SeedMeta(WorkspaceAId, ConnectionStatus.ReauthRequired);
        var post = SeedPost(WorkspaceAId, page.Id, PostStatus.RetryPending);

        Assert.DoesNotContain(post.Id, await QueryDuePostIdsAsync());

        // Reconnect recovery: flip connection + asset back to Active.
        conn.Status = ConnectionStatus.Active;
        page.Status = ConnectionStatus.Active;
        await _db.SaveChangesAsync();

        Assert.Contains(post.Id, await QueryDuePostIdsAsync());
    }

    [Fact]
    public void PublishGate_blocks_reauth_required_but_allows_active()
    {
        var active = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, PageId = "p", Name = "n",
            AccessToken = "t", CreatedAt = DateTime.UtcNow, IsConnected = true, Status = ConnectionStatus.Active,
        };
        var reauth = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, PageId = "p2", Name = "n",
            AccessToken = "t", CreatedAt = DateTime.UtcNow, IsConnected = true, Status = ConnectionStatus.ReauthRequired,
        };
        var disconnected = new ConnectedPage
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, PageId = "p3", Name = "n",
            AccessToken = "t", CreatedAt = DateTime.UtcNow, IsConnected = false, Status = ConnectionStatus.Active,
        };

        // Active asset, no parent → publishable (gate returns false = "not blocked by reauth").
        Assert.False(PublishGate.IsReauthRequired(active, null));
        // ReauthRequired asset → blocked.
        Assert.True(PublishGate.IsReauthRequired(reauth, null));
        // Active asset but parent connection ReauthRequired → blocked (parent gate).
        Assert.True(PublishGate.IsReauthRequired(active, new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = WorkspaceAId, UserId = Guid.NewGuid(),
            AccessToken = "t", IsConnected = true, Status = ConnectionStatus.ReauthRequired,
        }));
        // Disconnected asset is not a reauth case (handled by IsConnected checks elsewhere).
        Assert.False(PublishGate.IsReauthRequired(disconnected, null));
    }

    // ── 4. Ownership still blocks another workspace while ReauthRequired ───────

    [Fact]
    public async Task ReauthRequired_still_blocks_other_workspace_ownership()
    {
        SeedMeta(WorkspaceAId, ConnectionStatus.ReauthRequired, pageId: "shared-page", accountId: "shared-acct");

        var handler = new MetaProviderLifecycleHandler(
            _db, _schedulerMock.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        var providerService = new ProviderConnectionService(
            _db, new[] { (IProviderLifecycleHandler)handler }, NullLogger<ProviderConnectionService>.Instance);

        // Account-level + asset-level both still blocked while owner is ReauthRequired.
        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => providerService.EnsureNotOwnedByAnotherWorkspaceAsync(
                WorkspaceBId, ProviderType.Meta, "shared-acct", new[] { "shared-page" }));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Meta should not be called when gate passes in this test path");
    }
}
