using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Thrown when a user tries to connect a provider in a workspace that already
/// has an active connection for the same provider. The controller layer maps this
/// to a 409 with the spec-mandated message:
///
///     "This workspace already has a connected {Provider} account.
///      Disconnect it before connecting another one."
///
/// We DO NOT silently replace the existing connection. The user must explicitly
/// disconnect first.
/// </summary>
public class ProviderAlreadyConnectedException : InvalidOperationException
{
    public ProviderType Provider { get; }

    public ProviderAlreadyConnectedException(ProviderType provider)
        : base($"This workspace already has a connected {provider} account. " +
               "Disconnect it before connecting another one.")
    {
        Provider = provider;
    }
}
