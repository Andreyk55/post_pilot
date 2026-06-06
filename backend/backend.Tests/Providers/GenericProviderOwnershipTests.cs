using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PostPilot.Api.Data;
using PostPilot.Api.Entities;
using PostPilot.Api.Enums;
using PostPilot.Api.Services.Providers;
using PostPilot.Api.Services.Scheduling;
using Xunit;

namespace PostPilot.Api.Tests.Providers;

/// <summary>
/// PROVIDER-AGNOSTIC ownership tests. These prove the permanent-ownership rule lives
/// in the generic provider infrastructure (<see cref="ProviderConnectionService"/>),
/// NOT in Meta-only code, by exercising it with a NON-Meta provider
/// (<see cref="ProviderType.LinkedIn"/>) that has NO lifecycle handler registered and
/// NO provider-specific OAuth path.
///
/// The account-ownership rule depends ONLY on the generic columns
/// (<c>Provider</c> + <c>ProviderAccountId</c>) on the shared connection table. It does
/// not read PageId / IgBusinessId / any Meta field, so any future provider that records
/// its connection through this service inherits the rule automatically.
///
/// The rule is enforced by:
///   - service guard: <see cref="ProviderConnectionService.EnsureNotOwnedByAnotherWorkspaceAsync"/>
///     and <see cref="ProviderConnectionService.EnsureAccountMatchesWorkspaceBindingAsync"/>,
///     both reachable via <see cref="ProviderConnectionService.ValidateIncomingProviderAccountForWorkspaceAsync"/>;
///   - DB index: unique(Provider, ProviderAccountId) WHERE ProviderAccountId IS NOT NULL.
///
/// Scenarios mirror the validation spec, section D:
///   1. Meta: cross-user same account → rejected
///   2. LinkedIn: cross-user same account → rejected
///   3. Same ProviderAccountId, DIFFERENT provider → allowed
///   4. LinkedIn disconnected ownership → still rejected
///   5. Original workspace reconnect → allowed
///   6. Workspace-provider binding (same ws, different account) → rejected
///   7. No user/workspace/account leakage in the rejection
/// </summary>
public class GenericProviderOwnershipTests : IDisposable
{
    private static readonly Guid User1Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid User2Id = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Workspace1Id = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid Workspace2Id = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    private const string AccountA = "provider-account-A";
    private const string AccountB = "provider-account-B";

    private readonly AppDbContext _db;
    private readonly IProviderConnectionService _service;

    public GenericProviderOwnershipTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);

        // NOTE: only the Meta lifecycle handler is registered — exactly as production
        // does today. The LinkedIn scenarios below run with NO handler for LinkedIn,
        // proving the ACCOUNT-ownership rule does not require a provider handler at all
        // (the handler is only consulted for asset-level conflicts).
        var scheduler = new Mock<IPostScheduler>();
        var metaHandler = new MetaProviderLifecycleHandler(
            _db, scheduler.Object, NullLogger<MetaProviderLifecycleHandler>.Instance);
        _service = new ProviderConnectionService(
            _db, new[] { (IProviderLifecycleHandler)metaHandler },
            NullLogger<ProviderConnectionService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>Seed a connection row for ANY provider using only the generic identity columns.</summary>
    private MetaConnection SeedConnection(
        Guid workspaceId, Guid userId, ProviderType provider, string providerAccountId,
        bool isConnected = true, string? providerAccountName = "Owner Display Name")
    {
        var now = DateTime.UtcNow;
        var conn = new MetaConnection
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, UserId = userId,
            Provider = provider, ProviderAccountId = providerAccountId,
            ProviderAccountName = providerAccountName, AccessToken = isConnected ? "token" : null,
            TokenExpiresAt = now.AddDays(30), ConnectedAt = now, UpdatedAt = now,
            IsConnected = isConnected, Status = ConnectionStatus.Active,
            DisconnectedAt = isConnected ? null : now,
        };
        _db.MetaConnections.Add(conn);
        _db.SaveChanges();
        return conn;
    }

    // ── 1. Meta: cross-user same account → rejected ───────────────────────────────

    [Fact]
    public async Task Meta_cross_user_same_account_is_rejected()
    {
        SeedConnection(Workspace1Id, User1Id, ProviderType.Meta, AccountA);

        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _service.ValidateIncomingProviderAccountForWorkspaceAsync(
                Workspace2Id, ProviderType.Meta, AccountA));
    }

    // ── 2. LinkedIn: cross-user same account → rejected (genericity proof) ─────────

    [Fact]
    public async Task LinkedIn_cross_user_same_account_is_rejected_with_no_handler()
    {
        // No LinkedIn lifecycle handler is registered, yet account ownership is still
        // enforced — proving the rule is generic infrastructure, not provider code.
        SeedConnection(Workspace1Id, User1Id, ProviderType.LinkedIn, AccountA);

        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _service.ValidateIncomingProviderAccountForWorkspaceAsync(
                Workspace2Id, ProviderType.LinkedIn, AccountA));
    }

    // ── 3. Same ProviderAccountId, DIFFERENT provider → allowed ───────────────────

    [Fact]
    public async Task Same_account_id_on_different_provider_is_allowed()
    {
        // Ownership is keyed on (Provider + ProviderAccountId). Identical id "123" under
        // a different Provider is a different identity and must NOT collide.
        SeedConnection(Workspace1Id, User1Id, ProviderType.Meta, "123");

        // Workspace 2 connecting LinkedIn "123" is fine — different provider.
        await _service.ValidateIncomingProviderAccountForWorkspaceAsync(
            Workspace2Id, ProviderType.LinkedIn, "123");
    }

    // ── 4. LinkedIn disconnected ownership → still rejected ───────────────────────

    [Fact]
    public async Task LinkedIn_ownership_survives_disconnect_and_blocks_other_user()
    {
        // User 1 connected LinkedIn A then disconnected. Ownership is PERMANENT and is
        // NOT scoped by IsConnected, so user 2 still cannot claim it.
        SeedConnection(Workspace1Id, User1Id, ProviderType.LinkedIn, AccountA, isConnected: false);

        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _service.ValidateIncomingProviderAccountForWorkspaceAsync(
                Workspace2Id, ProviderType.LinkedIn, AccountA));
        Assert.DoesNotContain("Disconnect", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 5. Original workspace reconnect → allowed ─────────────────────────────────

    [Fact]
    public async Task Original_workspace_can_reconnect_same_account()
    {
        SeedConnection(Workspace1Id, User1Id, ProviderType.LinkedIn, AccountA, isConnected: false);

        // Same workspace, same account → no throw (binding matches, owner is self).
        await _service.ValidateIncomingProviderAccountForWorkspaceAsync(
            Workspace1Id, ProviderType.LinkedIn, AccountA);
    }

    // ── 6. Workspace-provider binding: same ws, different account → rejected ───────

    [Fact]
    public async Task Workspace_bound_to_account_A_cannot_switch_to_account_B()
    {
        // Workspace 1 connected LinkedIn A then disconnected; the workspace is now
        // PERMANENTLY bound to A for LinkedIn. Trying B in the same workspace is rejected.
        SeedConnection(Workspace1Id, User1Id, ProviderType.LinkedIn, AccountA, isConnected: false);

        var ex = await Assert.ThrowsAsync<ProviderAccountMismatchException>(
            () => _service.ValidateIncomingProviderAccountForWorkspaceAsync(
                Workspace1Id, ProviderType.LinkedIn, AccountB));
        Assert.Equal(AccountA, ex.BoundAccountId);
        Assert.Equal(AccountB, ex.AttemptedAccountId);
    }

    // ── 7. No user/workspace/account leakage in the rejection ─────────────────────

    [Fact]
    public async Task Rejection_message_does_not_leak_owner_identity()
    {
        SeedConnection(Workspace1Id, User1Id, ProviderType.LinkedIn, AccountA,
            providerAccountName: "Acme Corp LinkedIn");

        var ex = await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _service.ValidateIncomingProviderAccountForWorkspaceAsync(
                Workspace2Id, ProviderType.LinkedIn, AccountA));

        // The user-facing message is the fixed generic copy — never the owner's user id,
        // workspace id, account id, or account name.
        Assert.Equal(ProviderOwnedByAnotherWorkspaceException.UserMessage, ex.Message);
        Assert.DoesNotContain(User1Id.ToString(), ex.Message);
        Assert.DoesNotContain(Workspace1Id.ToString(), ex.Message);
        Assert.DoesNotContain(AccountA, ex.Message);
        Assert.DoesNotContain("Acme Corp LinkedIn", ex.Message);
    }

    // ── Bonus: account-rule does not depend on any asset/Meta field ───────────────

    [Fact]
    public async Task Account_rule_holds_even_with_no_assets_and_no_handler()
    {
        // A LinkedIn connection with ZERO ConnectedPages / ConnectedInstagramAccounts —
        // i.e. none of the Meta-specific asset tables are involved — still owns its
        // account globally. Proves PageId / IgBusinessId are irrelevant to the rule.
        SeedConnection(Workspace1Id, User1Id, ProviderType.LinkedIn, AccountA);
        Assert.False(await _db.ConnectedPages.AnyAsync());
        Assert.False(await _db.ConnectedInstagramAccounts.AnyAsync());

        await Assert.ThrowsAsync<ProviderOwnedByAnotherWorkspaceException>(
            () => _service.EnsureNotOwnedByAnotherWorkspaceAsync(
                Workspace2Id, ProviderType.LinkedIn, AccountA, Array.Empty<string>()));
    }
}
