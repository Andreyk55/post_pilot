using PostPilot.Api.Entities;

namespace PostPilot.Api.Services.Auth;

/// <summary>
/// Finds-or-creates an <see cref="AppUser"/> for an external identity, and
/// guarantees the user has at least one workspace where they are Owner.
/// Called from the Google OAuth callback after the provider has validated
/// the user — the identity claims passed in are trusted at this point.
/// </summary>
public interface IUserProvisioningService
{
    Task<ProvisionedUser> ProvisionAsync(ExternalIdentity identity, CancellationToken ct = default);
}

/// <summary>
/// Trusted claim set from a verified external provider. Email and DisplayName
/// are not unique identifiers; <see cref="Provider"/> + <see cref="ExternalUserId"/>
/// is the only stable key.
/// </summary>
public record ExternalIdentity(
    string Provider,
    string ExternalUserId,
    string Email,
    string DisplayName,
    string? AvatarUrl);

/// <summary>
/// Result of provisioning. <see cref="DefaultWorkspaceId"/> is the workspace
/// the user will be acting in until a switching UI exists.
/// </summary>
public record ProvisionedUser(
    AppUser User,
    Guid DefaultWorkspaceId,
    string DefaultWorkspaceName,
    bool IsNewUser);
