using PostPilot.Api.Services.Media;
using PostPilot.Api.Services.Validation;

namespace PostPilot.Api.Services.DataDeletion;

/// <summary>
/// Default <see cref="IStorageDeletionService"/> over the existing
/// <see cref="IMediaStorageProvider"/>. Deduplicates keys, enforces the allowed-prefix
/// guard, and never throws on a normal storage failure.
/// </summary>
public sealed class StorageDeletionService : IStorageDeletionService
{
    private readonly IMediaStorageProvider _storage;
    private readonly ILogger<StorageDeletionService> _logger;

    public StorageDeletionService(IMediaStorageProvider storage, ILogger<StorageDeletionService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task<StorageDeletionResult> DeleteObjectsBestEffortAsync(
        IReadOnlyCollection<string?> storageKeys,
        IReadOnlyCollection<string> allowedPrefixes,
        CancellationToken ct)
    {
        var prefixes = allowedPrefixes
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        var distinctKeys = storageKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var warnings = new List<string>();
        var deleted = 0;
        var skippedUnsafe = 0;

        foreach (var key in distinctKeys)
        {
            ct.ThrowIfCancellationRequested();

            if (prefixes.Length == 0 || !IsAllowed(key, prefixes))
            {
                // Defensive: a key that does not match an allowed prefix is NEVER deleted.
                skippedUnsafe++;
                _logger.LogWarning(
                    "Skipping storage delete for key outside allowed prefixes: {Key}",
                    RedactKey(key));
                continue;
            }

            try
            {
                await _storage.DeleteAsync(key, ct);
                deleted++;
            }
            catch (Exception ex)
            {
                // Best-effort: a storage failure must not abort the DB purge.
                warnings.Add($"Failed to delete storage object {RedactKey(key)}.");
                _logger.LogWarning(ex, "Best-effort storage delete failed for key {Key}", RedactKey(key));
            }
        }

        return new StorageDeletionResult(deleted, skippedUnsafe, warnings);
    }

    private static bool IsAllowed(string key, IReadOnlyCollection<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Reuse the gate's redaction so keys never appear raw in logs.
    private static string RedactKey(string? key) => MediaValidationGate.RedactKey(key);
}
