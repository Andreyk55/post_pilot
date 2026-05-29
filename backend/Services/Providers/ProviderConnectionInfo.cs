using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Providers;

/// <summary>
/// Identity snapshot of a workspace's active provider connection. Carried back
/// from <see cref="IProviderConnectionService"/> so callers can reason about
/// "which provider account is currently active" without depending on the
/// underlying entity type (today MetaConnection; future LinkedInConnection, etc.).
/// </summary>
public record ProviderConnectionInfo(
    Guid ConnectionId,
    Guid WorkspaceId,
    ProviderType Provider,
    string? ProviderAccountId,
    string? ProviderAccountName,
    Guid ConnectedByUserId,
    DateTime ConnectedAt
);
