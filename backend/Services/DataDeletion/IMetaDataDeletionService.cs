using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Formal, provider-level Meta purge. This is the STRONG deletion path required by
/// Meta's Data Deletion Callback — distinct from the soft in-app "Disconnect Meta"
/// (which keeps identity + history). It hard-deletes the matched MetaConnection,
/// its pages/IG accounts, every Meta post + media row, OAuth state, and the backing
/// storage objects.
///
/// It deliberately does NOT: verify signed_request, touch HTTP, delete the PostPilot
/// AppUser / workspace, delete non-Meta provider data, or call Graph to delete posts
/// already published on Facebook/Instagram.
/// </summary>
public interface IMetaDataDeletionService
{
    /// <summary>
    /// Purges all Meta data attached to the connection whose
    /// <c>(Provider=Meta, ProviderAccountId)</c> matches <paramref name="providerAccountId"/>.
    /// Looks across ALL workspaces, connected AND disconnected. A null/empty id or no
    /// match returns <see cref="DataDeletionStatus.AlreadyDeleted"/> (idempotent no-op).
    /// </summary>
    Task<MetaDataDeletionResult> PurgeByProviderAccountIdAsync(
        string? providerAccountId,
        CancellationToken ct);
}

/// <summary>
/// Outcome of a Meta purge. <see cref="DeletedCounts"/> is keyed by row type
/// ("Posts", "ConnectedPages", …). No identifying content beyond the resolved
/// user/workspace ids (used by the request service for internal audit).
/// </summary>
public sealed record MetaDataDeletionResult(
    DataDeletionStatus Status,
    Guid? UserId,
    Guid? WorkspaceId,
    IReadOnlyDictionary<string, int> DeletedCounts,
    IReadOnlyList<string> Warnings)
{
    public static MetaDataDeletionResult AlreadyDeleted() => new(
        DataDeletionStatus.AlreadyDeleted, null, null,
        new Dictionary<string, int>(), Array.Empty<string>());
}
