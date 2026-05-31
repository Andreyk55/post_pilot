using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Thrown when a workspace tries to connect a provider account or asset
/// (page / IG account / future LinkedIn page) that is currently OWNED by a
/// DIFFERENT workspace — i.e. an active or reauth-required connection/asset
/// with the same (Provider + ExternalAccountId) or (Provider + ExternalAssetId)
/// already exists elsewhere.
///
/// The controller layer maps this to a 409 with the spec-mandated message:
///
///     "This social account is already connected to another workspace.
///      Disconnect it there before connecting it here."
///
/// We never modify, disconnect, or move the owning workspace's data.
/// </summary>
public class ProviderOwnedByAnotherWorkspaceException : InvalidOperationException
{
    public const string UserMessage =
        "This social account is already connected to another workspace. " +
        "Disconnect it there before connecting it here.";

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
