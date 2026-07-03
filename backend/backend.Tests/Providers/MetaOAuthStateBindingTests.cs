using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Providers;

/// <summary>
/// M3: every Meta OAuth flow that consumes a state / temp-token must be bound to the caller's
/// current (server-resolved, membership-checked) workspace. A state minted for workspace B can
/// never be completed/discovered/saved from a workspace-A context, expired/invalid/reused
/// states fail closed, and rejections don't leak which workspace a state belongs to.
///
/// The "currentWorkspaceId" argument these tests pass is exactly what the controller passes:
/// the result of ICurrentWorkspaceProvider.GetCurrentWorkspaceIdAsync(), which re-checks
/// membership. Passing WorkspaceA here therefore models "caller is a member of A, currently in A".
/// </summary>
public class MetaOAuthStateBindingTests : IDisposable
{
    private const string GraphBase = "https://graph.facebook.com/v21.0";
    private static readonly Guid UserId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid WorkspaceA = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceB = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private readonly AppDbContext _db;

    public MetaOAuthStateBindingTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ── Happy path ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Callback_then_Save_succeed_in_the_states_own_workspace()
    {
        var (stateId, state) = SeedState(WorkspaceA);
        var service = NewService();

        // Callback in A succeeds and stores the temp token.
        var callback = await service.HandleCallbackAsync("code", state, WorkspaceA);
        Assert.NotNull(callback);
        Assert.False(string.IsNullOrEmpty(
            (await _db.MetaOAuthStates.AsNoTracking().SingleAsync(s => s.Id == stateId)).TempAccessToken));

        // Save in A succeeds and creates the connection for A.
        var save = await service.SaveConnectionAsync(
            stateId.ToString(), new List<string> { "page-1" }, new List<string>(), UserId, WorkspaceA);
        Assert.NotNull(save);
        Assert.True(await _db.MetaConnections.AsNoTracking().AnyAsync(c => c.WorkspaceId == WorkspaceA && c.IsConnected));
    }

    [Fact]
    public async Task Discover_succeeds_in_the_states_own_workspace()
    {
        var (stateId, _) = SeedState(WorkspaceA, withToken: true);
        var service = NewService();

        var result = await service.DiscoverInstagramAccountsAsync(
            stateId.ToString(), new List<string> { "page-1" }, WorkspaceA);

        Assert.NotNull(result);
    }

    // ── Negative: cross-workspace state is rejected ───────────────────────────────

    [Fact]
    public async Task Callback_rejected_when_state_belongs_to_another_workspace()
    {
        var (_, state) = SeedState(WorkspaceB);
        var service = NewService();

        await Assert.ThrowsAsync<OAuthStateAccessDeniedException>(
            () => service.HandleCallbackAsync("code", state, WorkspaceA));

        // Nothing was exchanged/persisted for A.
        Assert.Null((await _db.MetaOAuthStates.AsNoTracking().SingleAsync(s => s.State == state)).TempAccessToken);
    }

    [Fact]
    public async Task Complete_rejected_when_state_belongs_to_another_workspace()
    {
        var (_, state) = SeedState(WorkspaceB);
        var service = NewService();

        await Assert.ThrowsAsync<OAuthStateAccessDeniedException>(
            () => service.CompleteOAuthAsync("code", state, UserId, WorkspaceA));
    }

    [Fact]
    public async Task Discover_rejected_when_state_belongs_to_another_workspace()
    {
        var (stateId, _) = SeedState(WorkspaceB, withToken: true);
        var service = NewService();

        await Assert.ThrowsAsync<OAuthStateAccessDeniedException>(
            () => service.DiscoverInstagramAccountsAsync(stateId.ToString(), new List<string> { "page-1" }, WorkspaceA));
    }

    [Fact]
    public async Task Save_rejected_when_state_belongs_to_another_workspace()
    {
        var (stateId, _) = SeedState(WorkspaceB, withToken: true);
        var service = NewService();

        await Assert.ThrowsAsync<OAuthStateAccessDeniedException>(
            () => service.SaveConnectionAsync(
                stateId.ToString(), new List<string> { "page-1" }, new List<string>(), UserId, WorkspaceA));

        // No connection leaked into A.
        Assert.False(await _db.MetaConnections.AsNoTracking().AnyAsync(c => c.WorkspaceId == WorkspaceA));
    }

    // ── Replay / expiry / invalid ────────────────────────────────────────────────

    [Fact]
    public async Task Reusing_a_consumed_state_fails()
    {
        var (stateId, state) = SeedState(WorkspaceA);
        var service = NewService();

        await service.HandleCallbackAsync("code", state, WorkspaceA);
        await service.SaveConnectionAsync(
            stateId.ToString(), new List<string> { "page-1" }, new List<string>(), UserId, WorkspaceA);

        // Save consumed (deleted) the state — a second save with the same temp token fails closed.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveConnectionAsync(
                stateId.ToString(), new List<string> { "page-1" }, new List<string>(), UserId, WorkspaceA));
        Assert.Contains("Invalid or expired", ex.Message);
    }

    [Fact]
    public async Task Expired_state_callback_fails_closed()
    {
        var (_, state) = SeedState(WorkspaceA, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var service = NewService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.HandleCallbackAsync("code", state, WorkspaceA));
        Assert.Contains("Invalid or expired", ex.Message);
    }

    [Fact]
    public async Task Expired_temp_token_save_fails_closed()
    {
        var (stateId, _) = SeedState(WorkspaceA, expiresAt: DateTime.UtcNow.AddMinutes(-1), withToken: true);
        var service = NewService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveConnectionAsync(
                stateId.ToString(), new List<string> { "page-1" }, new List<string>(), UserId, WorkspaceA));
        Assert.Contains("Invalid or expired", ex.Message);
    }

    [Fact]
    public async Task Invalid_random_state_fails_safely_without_leaking()
    {
        var service = NewService();

        // Unknown state value → generic invalid/expired (NOT an access-denied leak).
        var ex1 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.HandleCallbackAsync("code", "totally-unknown-state", WorkspaceA));
        Assert.Contains("Invalid or expired", ex1.Message);

        // Unknown temp-token GUID for discover/save → generic invalid/expired.
        var randomTemp = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DiscoverInstagramAccountsAsync(randomTemp, new List<string> { "page-1" }, WorkspaceA));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveConnectionAsync(randomTemp, new List<string> { "page-1" }, new List<string>(), UserId, WorkspaceA));
    }

    // ── wiring ────────────────────────────────────────────────────────────────────

    private (Guid id, string state) SeedState(Guid workspaceId, DateTime? expiresAt = null, bool withToken = false)
    {
        var now = DateTime.UtcNow;
        var row = new MetaOAuthState
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            State = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            ExpiresAt = expiresAt ?? now.AddMinutes(10),
            TempAccessToken = withToken ? "temp-token" : null,
            TokenExpiresAt = withToken ? now.AddHours(1) : (DateTime?)null,
        };
        _db.MetaOAuthStates.Add(row);
        _db.SaveChanges();
        return (row.Id, row.State);
    }

    private MetaOAuthService NewService()
    {
        var scheduler = new Mock<IPostScheduler>();
        var lifecycle = new MetaProviderLifecycleHandler(
            _db, scheduler.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        var providerConnections = new ProviderConnectionService(
            _db, new IProviderLifecycleHandler[] { lifecycle },
            NullLogger<ProviderConnectionService>.Instance);

        return new MetaOAuthService(
            _db,
            new HttpClient(new FakeGraphHandler()),
            new MetaOptions { AppId = "test", AppSecret = "test", RedirectUri = "http://localhost/cb" },
            NullLogger<MetaOAuthService>.Instance,
            scheduler.Object,
            providerConnections,
            new MetaApiOptions(),
            new PublishingOptions { OAuthStateExpirationMinutes = 10 });
    }

    private sealed class FakeGraphHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            string body;
            if (url.Contains("/oauth/access_token"))
                body = "{\"access_token\":\"tok\",\"expires_in\":3600}";
            else if (url.Contains($"{GraphBase}/me/accounts"))
                body = "{\"data\":[{\"id\":\"page-1\",\"name\":\"Page One\",\"access_token\":\"pt\"}]}";
            else if (url.Contains("/me/permissions"))
                body = "{\"data\":[]}";
            else if (url.Contains($"{GraphBase}/me"))
                body = "{\"id\":\"meta-user-x\",\"name\":\"X\"}";
            else
                body = "{}"; // per-page IG query → no linked IG

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
