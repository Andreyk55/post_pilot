using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Thrown when a workspace tries to connect a provider account whose stable
/// external identity (<c>ProviderAccountId</c>) DIFFERS from the account this
/// workspace is already permanently bound to for that provider.
///
/// Product rule (permanent binding): the FIRST external account a workspace
/// connects for a given provider becomes its permanent identity for that
/// provider. After a disconnect the workspace may reconnect ONLY that same
/// account — connecting a different account is forbidden, even though the row
/// is disconnected. This is stricter than <see cref="ProviderAlreadyConnectedException"/>
/// (which only fires while a connection is still active).
///
/// The controller layer maps this to a 409 with the message below. We never
/// rebind, delete, or overwrite the existing identity.
/// </summary>
public class ProviderAccountMismatchException : InvalidOperationException
{
    /// <summary>
    /// User-facing 409 copy. Generic ("provider account") and consistent with
    /// <see cref="ProviderOwnedByAnotherWorkspaceException.UserMessage"/>. Must NOT
    /// suggest that disconnecting elsewhere will allow the connection, and must NOT
    /// expose any workspace name or account id.
    /// </summary>
    public const string UserMessage =
        "This workspace is already linked to a different provider account. " +
        "Reconnect the original account for this workspace, or use another workspace.";

    public ProviderType Provider { get; }

    /// <summary>The account this workspace is permanently bound to (for diagnostics/logging).</summary>
    public string? BoundAccountId { get; }

    /// <summary>The different account the user just tried to connect (for diagnostics/logging).</summary>
    public string? AttemptedAccountId { get; }

    public ProviderAccountMismatchException(
        ProviderType provider,
        string? boundAccountId,
        string? attemptedAccountId)
        : base(UserMessage)
    {
        Provider = provider;
        BoundAccountId = boundAccountId;
        AttemptedAccountId = attemptedAccountId;
    }
}
