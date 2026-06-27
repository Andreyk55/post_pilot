namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Best-effort, prefix-guarded deletion of storage objects. The defensive prefix
/// check is the safety net that prevents a deletion path from ever removing an
/// object outside the caller's intended scope (wrong workspace, wrong provider
/// folder, or a key that isn't ours at all).
/// </summary>
public interface IStorageDeletionService
{
    /// <summary>
    /// Deletes each key that (a) is non-empty and (b) starts with one of
    /// <paramref name="allowedPrefixes"/>. Keys failing the guard are skipped and
    /// reported, not deleted. Normal storage failures are swallowed and recorded as
    /// warnings — never thrown — so an external storage hiccup cannot abort a DB purge.
    /// </summary>
    Task<StorageDeletionResult> DeleteObjectsBestEffortAsync(
        IReadOnlyCollection<string?> storageKeys,
        IReadOnlyCollection<string> allowedPrefixes,
        CancellationToken ct);
}

/// <summary>
/// Outcome of a best-effort storage deletion sweep. No object content or secrets.
/// </summary>
public sealed record StorageDeletionResult(
    int Deleted,
    int SkippedUnsafe,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0 || SkippedUnsafe > 0;
}
