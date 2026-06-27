namespace PostPilot.Api.Services.Account;

/// <summary>
/// Full PostPilot account deletion for the AUTHENTICATED current user only. The
/// target id is supplied by the controller from the auth principal — never from a
/// request body. The service deletes the user's AppUser/auth identity and every piece
/// of data in the workspaces they own (provider connections, Meta data, posts, drafts,
/// media rows, OAuth state, bucket files, memberships).
///
/// It never deletes other users/accounts/workspaces, and never calls Graph to delete
/// posts already published on Facebook/Instagram.
/// </summary>
public interface IAccountDeletionService
{
    Task DeleteCurrentAccountAsync(Guid authenticatedUserId, CancellationToken ct);
}
