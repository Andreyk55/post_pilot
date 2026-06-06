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
/// Proves the FAIL-FAST ownership rule on the OAuth callback path: as soon as the
/// backend resolves the external Meta account id (FetchMetaUserIdentityAsync), it
/// must validate permanent ownership and reject — BEFORE fetching pages, storing the
/// temp access token, or returning a page-selection list — when the account is
/// permanently owned by another workspace or this workspace is bound to a different
/// account.
///
/// These drive the real <see cref="MetaOAuthService.HandleCallbackAsync"/> against an
/// in-memory DB and a recording fake Graph handler, so we can assert exactly which
/// Graph endpoints were (not) called.
/// </summary>
public class ProviderOwnershipCallbackGuardTests : IDisposable
{
    private const string GraphBase = "https://graph.facebook.com/v21.0";

    private static readonly Guid UserAId = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid UserBId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid WorkspaceAId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid WorkspaceBId = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private const string MetaAccountAlpha = "meta-user-alpha";
    private const string MetaAccountBeta = "meta-user-beta";

    private readonly AppDbContext _db;
    private readonly RecordingHandler _handler;
    private readonly MetaOAuthService _service;

    public ProviderOwnershipCallbackGuardTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        // The /me identity always resolves to MetaAccountAlpha for these tests; the
        // token-exchange + debug calls return harmless stubs. /me/accounts (pages) is
        // wired but its invocation is what we assert MUST NOT happen on rejection.
        _handler = new RecordingHandler(meAccountId: MetaAccountAlpha);

        var scheduler = new Mock<IPostScheduler>();
        var lifecycleHandler = new MetaProviderLifecycleHandler(
            _db, scheduler.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        var providerConnections = new ProviderConnectionService(
            _db, new[] { (IProviderLifecycleHandler)lifecycleHandler },
            NullLogger<ProviderConnectionService>.Instance);

        _service = new MetaOAuthService(
            _db,
            new HttpClient(_handler),
            new MetaOptions { AppId = "test", AppSecret = "test", RedirectUri = "http://localhost/cb" },
            NullLogger<MetaOAuthService>.Instance,
            scheduler.Object,
            providerConnections,
            new MetaApiOptions(),
            new PublishingOptions { OAuthStateExpirationMinutes = 10 });
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SeedActiveMeta(Guid workspaceId, Guid userId, string providerAccountId)
    {
        var now = DateTime.UtcNow;
        _db.MetaConnections.Add(new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId,
            Provider = ProviderType.Meta, ProviderAccountId = providerAccountId,
            ProviderAccountName = providerAccountId, AccessToken = "user-token",
            TokenExpiresAt = now.AddDays(30), ConnectedAt = now, UpdatedAt = now,
            IsConnected = true, Status = ConnectionStatus.Active,
        });
        _db.SaveChanges();
    }

    /// <summary>Seed a fresh OAuth state for <paramref name="workspaceId"/> and return its state string.</summary>
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

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Callback_rejects_immediately_when_account_owned_by_another_workspace()
    {
        // Workspace A permanently owns account Alpha (still active).
        SeedActiveMeta(WorkspaceAId, UserAId, MetaAccountAlpha);

        // Workspace B starts an OAuth flow; the provider returns the SAME account Alpha.
        var state = SeedOAuthState(WorkspaceBId);

        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _service.HandleCallbackAsync("code", state));

        // The 409 message is the permanent-ownership message and never suggests
        // disconnecting from the other workspace.
        Assert.Equal(
            "This provider account is already permanently linked to another workspace. " +
            "To use a different account, create or select another workspace.",
            ex.Message);
        Assert.DoesNotContain("Disconnect", ex.Message);

        // Pages were NEVER fetched (fail-fast before page discovery / selection UI).
        Assert.False(_handler.PagesFetched, "Pages must not be fetched when ownership is rejected.");

        // No temp access token was persisted onto the OAuth state (no selection state created).
        var stateRow = await _db.MetaOAuthStates.AsNoTracking().FirstAsync(s => s.State == state);
        Assert.Null(stateRow.TempAccessToken);

        // No new connection/asset rows were created for workspace B.
        Assert.False(await _db.MetaConnections.AsNoTracking().AnyAsync(c => c.WorkspaceId == WorkspaceBId));
        Assert.False(await _db.ConnectedPages.AsNoTracking().AnyAsync(p => p.WorkspaceId == WorkspaceBId));
    }

    [Fact]
    public async Task Callback_rejects_when_account_owned_by_another_workspace_even_if_disconnected()
    {
        // Workspace A connected Alpha then DISCONNECTED — ownership is permanent.
        SeedActiveMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        await _service.DisconnectAsync(WorkspaceAId);
        _handler.Reset();

        var state = SeedOAuthState(WorkspaceBId);

        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _service.HandleCallbackAsync("code", state));
        Assert.Contains("permanently linked to another workspace", ex.Message);

        Assert.False(_handler.PagesFetched, "Pages must not be fetched when ownership is rejected.");
        var stateRow = await _db.MetaOAuthStates.AsNoTracking().FirstAsync(s => s.State == state);
        Assert.Null(stateRow.TempAccessToken);
        Assert.False(await _db.MetaConnections.AsNoTracking().AnyAsync(c => c.WorkspaceId == WorkspaceBId));
    }

    [Fact]
    public async Task Callback_rejects_when_workspace_is_bound_to_a_different_account()
    {
        // Workspace B is permanently bound to Beta; the provider now returns Alpha.
        SeedActiveMeta(WorkspaceBId, UserBId, MetaAccountBeta);
        await _service.DisconnectAsync(WorkspaceBId); // free the active slot; binding persists
        _handler.Reset();

        var state = SeedOAuthState(WorkspaceBId);

        var ex = await Assert.ThrowsAsync<ProviderAccountMismatchException>(
            () => _service.HandleCallbackAsync("code", state));
        Assert.Equal(MetaAccountBeta, ex.BoundAccountId);
        Assert.Equal(MetaAccountAlpha, ex.AttemptedAccountId);

        // Still fail-fast: no page fetch, no selection state.
        Assert.False(_handler.PagesFetched, "Pages must not be fetched when binding is violated.");
        var stateRow = await _db.MetaOAuthStates.AsNoTracking().FirstAsync(s => s.State == state);
        Assert.Null(stateRow.TempAccessToken);
    }

    [Fact]
    public async Task Callback_allows_and_fetches_pages_when_account_is_free()
    {
        // No prior owner; workspace A connects Alpha for the first time → allowed.
        var state = SeedOAuthState(WorkspaceAId);

        var response = await _service.HandleCallbackAsync("code", state);

        // The page-selection list is returned only after ownership passes.
        Assert.True(_handler.PagesFetched, "Pages should be fetched once ownership passes.");
        Assert.NotNull(response);
        Assert.Equal(state, (await _db.MetaOAuthStates.AsNoTracking().FirstAsync(s => s.State == state)).State);

        // Temp token now persisted so the subsequent SaveConnection can proceed.
        var stateRow = await _db.MetaOAuthStates.AsNoTracking().FirstAsync(s => s.State == state);
        Assert.False(string.IsNullOrEmpty(stateRow.TempAccessToken));
    }

    [Fact]
    public async Task Callback_allows_reconnect_in_original_workspace_after_disconnect()
    {
        // Workspace A owns Alpha, disconnects (ownership stays permanent for A), then
        // reconnects the SAME account in the SAME workspace via OAuth → allowed, and
        // pages are fetched only because ownership validation passed first.
        SeedActiveMeta(WorkspaceAId, UserAId, MetaAccountAlpha);
        await _service.DisconnectAsync(WorkspaceAId);
        _handler.Reset();

        var state = SeedOAuthState(WorkspaceAId);

        var response = await _service.HandleCallbackAsync("code", state);

        Assert.NotNull(response);
        Assert.True(_handler.PagesFetched, "Pages should be fetched on a valid same-workspace reconnect.");
        var stateRow = await _db.MetaOAuthStates.AsNoTracking().FirstAsync(s => s.State == state);
        Assert.False(string.IsNullOrEmpty(stateRow.TempAccessToken));
    }

    // ── Recording fake Graph handler ─────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _meAccountId;
        public bool PagesFetched { get; private set; }

        public RecordingHandler(string meAccountId) => _meAccountId = meAccountId;

        public void Reset() => PagesFetched = false;

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
                // Page discovery — must only be hit when ownership passes.
                PagesFetched = true;
                body = "{\"data\":[{\"id\":\"page-1\",\"name\":\"Page One\",\"access_token\":\"pt\"}]}";
            }
            else if (url.Contains("/me/permissions"))
            {
                body = "{\"data\":[]}";
            }
            else if (url.Contains($"{GraphBase}/me"))
            {
                // Identity resolution (/me?fields=id,name).
                body = $"{{\"id\":\"{_meAccountId}\",\"name\":\"Alpha User\"}}";
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
