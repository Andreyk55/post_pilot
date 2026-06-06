using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Thrown when a workspace tries to connect a provider account or asset
/// (page / IG account / future LinkedIn page) that is PERMANENTLY OWNED by a
/// DIFFERENT workspace — i.e. a row with the same (Provider + ExternalAccountId)
/// or (Provider + ExternalAssetId) already exists in another workspace.
///
/// Account-level ownership is PERMANENT: the first workspace to connect a
/// provider account identity owns it forever. Disconnecting there does NOT
/// release it, so the same external account can never be connected to another
/// workspace later. (Asset-level ownership for pages / IG accounts is released
/// on disconnect, but the permanent account binding makes that moot for cross-
/// workspace claims.)
///
/// The controller layer maps this to a 409 with the spec-mandated message below.
/// We never modify, disconnect, or move the owning workspace's data.
/// </summary>
public class ProviderOwnedByAnotherWorkspaceException : InvalidOperationException
{
    public const string UserMessage =
        "This provider account is already linked to another workspace. " +
        "Select the original workspace for this account, or connect a different provider account.";

    public ProviderType Provider { get; }

    /// <summary>The external account/asset id that is owned elsewhere (for diagnostics/logging).</summary>
    public string? ConflictingExternalId { get; }

    public ProviderOwnedByAnotherWorkspaceException(ProviderType provider, string? conflictingExternalId = null)
        : base(UserMessage)
    {
        Provider = provider;
        ConflictingExternalId = conflictingExternalId;
    }
}
