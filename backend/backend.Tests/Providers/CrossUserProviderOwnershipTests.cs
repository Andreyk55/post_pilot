using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.DTOs;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Scheduling;
using PostPilot.Api.Settings;
using Xunit;

namespace PostPilot.Api.Tests.Providers;

/// <summary>
/// CROSS-POSTPILOT-USER permanent ownership. The ownership rule must be GLOBAL: it is
/// keyed only on (Provider + ProviderAccountId) across ALL connections regardless of
/// UserId and regardless of IsConnected. The first workspace to connect an account
/// owns it forever — even if a DIFFERENT PostPilot user, in a DIFFERENT workspace,
/// later attempts the same account, and even after the original owner disconnected.
///
/// These drive the real <see cref="MetaOAuthService.HandleCallbackAsync"/> (the
/// fail-fast callback path) against an in-memory DB, with the Graph /me identity
/// pinned per-test, so user 2's attempt resolves to the SAME external account id as
/// user 1's existing connection.
///
/// Scenarios mirror the validation spec:
///   A. Different users, same account, first ACTIVE      → 409
///   B. Different users, same account, first DISCONNECTED → 409
///   C. Original user/workspace reconnect                 → success
///   D. Different user, DIFFERENT account                 → success
///   E. No user/workspace leakage in the 409 body
/// </summary>
public class CrossUserProviderOwnershipTests : IDisposable
{
    private const string GraphBase = "https://graph.facebook.com/v21.0";

    // Two DISTINCT PostPilot users, each owning a DISTINCT workspace.
    private static readonly Guid User1Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid User2Id = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Workspace1Id = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid Workspace2Id = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private const string MetaAccountA = "meta-account-A";
    private const string MetaAccountB = "meta-account-B";

    private readonly AppDbContext _db;

    public CrossUserProviderOwnershipTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Build a MetaOAuthService whose Graph /me always resolves to <paramref name="meAccountId"/>.</summary>
    private (MetaOAuthService service, RecordingHandler handler) NewService(string meAccountId)
    {
        var handler = new RecordingHandler(meAccountId);
        var scheduler = new Mock<IPostScheduler>();
        var lifecycleHandler = new MetaProviderLifecycleHandler(
            _db, scheduler.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        var providerConnections = new ProviderConnectionService(
            _db, new[] { (IProviderLifecycleHandler)lifecycleHandler },
            NullLogger<ProviderConnectionService>.Instance);

        var service = new MetaOAuthService(
            _db,
            new HttpClient(handler),
            new MetaOptions { AppId = "test", AppSecret = "test", RedirectUri = "http://localhost/cb" },
            NullLogger<MetaOAuthService>.Instance,
            scheduler.Object,
            providerConnections,
            new MetaApiOptions(),
            new PublishingOptions { OAuthStateExpirationMinutes = 10 });
        return (service, handler);
    }

    private void SeedActiveMeta(Guid workspaceId, Guid userId, string providerAccountId)
    {
        var now = DateTime.UtcNow;
        _db.MetaConnections.Add(new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId,
            Provider = ProviderType.Meta, ProviderAccountId = providerAccountId,
            ProviderAccountName = "Original Owner Display Name", AccessToken = "user-token",
            TokenExpiresAt = now.AddDays(30), ConnectedAt = now, UpdatedAt = now,
            IsConnected = true, Status = ConnectionStatus.Active,
        });
        _db.SaveChanges();
    }

    private string SeedOAuthState(Guid workspaceId)
    {
        var state = Guid.NewGuid().ToString("N");
        _db.MetaOAuthStates.Add(new MetaOAuthState
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            State = state,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        _db.SaveChanges();
        return state;
    }

    /// <summary>
    /// Runs the callback bound to the state's OWN workspace — i.e. the legitimate case where the
    /// caller's current (membership-checked) workspace equals the state's workspace. These tests
    /// exercise permanent provider-ownership rules, not the M3 workspace-mismatch guard (which is
    /// covered by MetaOAuthStateBindingTests).
    /// </summary>
    private async Task<MetaOAuthCallbackResponse> Callback(MetaOAuthService service, string state)
    {
        var ws = (await _db.MetaOAuthStates.AsNoTracking().SingleAsync(s => s.State == state)).WorkspaceId;
        return await service.HandleCallbackAsync("code", state, ws);
    }

    // ── Scenario A: different users, same account, first ACTIVE → 409 ────────────

    [Fact]
    public async Task A_user2_cannot_connect_account_owned_by_user1_while_active()
    {
        // User 1 / Workspace 1 actively owns account A.
        SeedActiveMeta(Workspace1Id, User1Id, MetaAccountA);

        // User 2 / Workspace 2 starts OAuth; the provider returns the SAME account A.
        var (service, handler) = NewService(MetaAccountA);
        var state = SeedOAuthState(Workspace2Id);

        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => Callback(service, state));

        // Fail-fast: never fetched pages, never persisted a connection for workspace 2.
        Assert.False(handler.PagesFetched);
        Assert.False(await _db.MetaConnections.AsNoTracking().AnyAsync(c => c.WorkspaceId == Workspace2Id));
    }

    // ── Scenario B: different users, same account, first DISCONNECTED → 409 ───────

    [Fact]
    public async Task B_user2_cannot_connect_account_after_user1_disconnected()
    {
        // User 1 connects account A, then disconnects. Ownership is PERMANENT — the
        // disconnect must NOT release the account to another PostPilot user.
        SeedActiveMeta(Workspace1Id, User1Id, MetaAccountA);
        var (svc1, _) = NewService(MetaAccountA);
        await svc1.DisconnectAsync(Workspace1Id);

        var (service, handler) = NewService(MetaAccountA);
        var state = SeedOAuthState(Workspace2Id);

        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => Callback(service, state));

        // Must NOT suggest disconnecting elsewhere will free the account.
        Assert.DoesNotContain("Disconnect", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(handler.PagesFetched);
        Assert.False(await _db.MetaConnections.AsNoTracking().AnyAsync(c => c.WorkspaceId == Workspace2Id));
    }

    // ── Scenario C: original user/workspace reconnect → success ───────────────────

    [Fact]
    public async Task C_user1_can_reconnect_its_own_account_after_disconnect()
    {
        SeedActiveMeta(Workspace1Id, User1Id, MetaAccountA);
        var (svc1, _) = NewService(MetaAccountA);
        await svc1.DisconnectAsync(Workspace1Id);

        // Same user, same workspace, same account → allowed, pages fetched.
        var (service, handler) = NewService(MetaAccountA);
        var state = SeedOAuthState(Workspace1Id);

        var response = await Callback(service, state);

        Assert.NotNull(response);
        Assert.True(handler.PagesFetched);
    }

    // ── Scenario D: different user, DIFFERENT account → success ───────────────────

    [Fact]
    public async Task D_user2_can_connect_a_different_account()
    {
        // User 1 owns account A (active). User 2 brings a DISTINCT account B.
        SeedActiveMeta(Workspace1Id, User1Id, MetaAccountA);

        var (service, handler) = NewService(MetaAccountB);
        var state = SeedOAuthState(Workspace2Id);

        var response = await Callback(service, state);

        Assert.NotNull(response);
        Assert.True(handler.PagesFetched);
        // User 1's account A is untouched and still owned by workspace 1.
        Assert.True(await _db.MetaConnections.AsNoTracking()
            .AnyAsync(c => c.WorkspaceId == Workspace1Id && c.ProviderAccountId == MetaAccountA));
    }

    // ── Scenario E: no user/workspace leakage in the 409 ──────────────────────────

    [Fact]
    public async Task E_rejection_does_not_leak_owner_identity()
    {
        // Seed user 1's connection with identifying display data, then reject user 2.
        SeedActiveMeta(Workspace1Id, User1Id, MetaAccountA);

        var (service, _) = NewService(MetaAccountA);
        var state = SeedOAuthState(Workspace2Id);

        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => Callback(service, state));

        // The message shown to user 2 is the generic, fixed UserMessage — it must not
        // expose the original owner's user id, workspace id/name, account id, or name.
        Assert.Equal(ProviderOwnedByAnotherWorkspaceException.UserMessage, ex.Message);
        Assert.DoesNotContain(User1Id.ToString(), ex.Message);
        Assert.DoesNotContain(Workspace1Id.ToString(), ex.Message);
        Assert.DoesNotContain(MetaAccountA, ex.Message);
        Assert.DoesNotContain("Original Owner Display Name", ex.Message);
    }

    // ── Recording fake Graph handler ─────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _meAccountId;
        public bool PagesFetched { get; private set; }

        public RecordingHandler(string meAccountId) => _meAccountId = meAccountId;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            string body;
            if (url.Contains("/oauth/access_token"))
            {
                body = "{\"access_token\":\"tok\",\"expires_in\":3600}";
            }
            else if (url.Contains($"{GraphBase}/me/accounts"))
            {
                PagesFetched = true;
                body = "{\"data\":[{\"id\":\"page-1\",\"name\":\"Page One\",\"access_token\":\"pt\"}]}";
            }
            else if (url.Contains("/me/permissions"))
            {
                body = "{\"data\":[]}";
            }
            else if (url.Contains($"{GraphBase}/me"))
            {
                body = $"{{\"id\":\"{_meAccountId}\",\"name\":\"Some User\"}}";
            }
            else
            {
                body = "{}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
