using PostPilot.Api.Enums;

namespace PostPilot.Api.Services.Validation;

/// <summary>
/// Authoritative server-side media validation used to GATE post creation/scheduling and
/// to guard publishers right before a Meta call. Unlike the stateless /api/media/validate
/// endpoint (advisory, frontend-driven), this is the enforcement point: a crafted or
/// replayed request that bypasses the SPA must still be rejected here.
///
/// <para>
/// Phase 2 scope: IMAGES only. Video items are passed through (treated as valid) so this
/// change is purely additive for the image path. PNG→JPEG conversion, upload decoupling,
/// and storage-key changes are explicitly out of scope for this phase.
/// </para>
/// </summary>
public interface IMediaValidationGate
{
    /// <summary>
    /// Validates every supplied media item against every target (platform + placement).
    /// Resolves the real content-type/size from the workspace's <c>Media</c> rows (never
    /// trusts a client-supplied MIME), downloads the bytes once per item, and runs the
    /// shared <see cref="IMediaValidationService"/> rules.
    ///
    /// <para>Errors block; warnings do not. The result aggregates a flat list of
    /// per-(item,target) failures so the caller can surface exactly which media item failed
    /// for which platform/placement and why.</para>
    /// </summary>
    /// <param name="workspaceId">Workspace that owns the media (storage-key ownership is enforced here).</param>
    /// <param name="items">Media items to validate (storage key + media type + 0-based order).</param>
    /// <param name="targets">Targets to validate against (platform + placement).</param>
    Task<MediaGateResult> ValidateAsync(
        Guid workspaceId,
        IReadOnlyList<MediaGateItem> items,
        IReadOnlyList<MediaGateTarget> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Single-item, single-target convenience guard for publishers. Returns the first
    /// blocking error message (warnings ignored), or null when the item is publishable.
    /// Never throws on storage/decode problems for legacy media — see implementation notes.
    /// </summary>
    Task<string?> ValidateSingleAsync(
        Guid workspaceId,
        MediaGateItem item,
        MediaGateTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>One attached media item to validate. <see cref="StorageKeyOrUrl"/> is the value stored in Post.MediaUrl / PostMediaItem.MediaUrl.</summary>
public record MediaGateItem(string StorageKeyOrUrl, MediaType MediaType, int Order);

/// <summary>A publishing target to validate against.</summary>
public record MediaGateTarget(Platform Platform, Placement Placement);

/// <summary>A single per-(item,target) validation failure.</summary>
public record MediaGateError(
    int Order,
    string StorageKeyRedacted,
    Platform Platform,
    Placement Placement,
    string Code,
    string Field,
    string Message);

/// <summary>Aggregate result. <see cref="IsValid"/> is true when there are no blocking errors.</summary>
public record MediaGateResult(IReadOnlyList<MediaGateError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static MediaGateResult Valid { get; } = new(Array.Empty<MediaGateError>());
}
