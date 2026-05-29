namespace PostPilot.Api.Enums;

/// <summary>
/// Why a post landed in <see cref="PostStatus.Canceled"/>.
/// Persisted on the Post row alongside CanceledAt so we can answer
/// "why was this canceled" without parsing the human ErrorMessage.
/// </summary>
public enum CancellationReason
{
    /// <summary>Default for legacy/pre-migration rows.</summary>
    None = 0,

    /// <summary>User-initiated cancel through the UI/API.</summary>
    UserCanceled = 1,

    /// <summary>Provider account was disconnected from the workspace.</summary>
    ProviderDisconnected = 2,

    /// <summary>A specific provider asset (page / IG account) was unlinked
    /// while the parent provider connection stayed connected.</summary>
    ProviderAssetUnlinked = 3,
}
