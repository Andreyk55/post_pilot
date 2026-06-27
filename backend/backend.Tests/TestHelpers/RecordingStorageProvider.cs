using PostPilot.Api.Services.Media;

namespace PostPilot.Api.Tests.TestHelpers;

/// <summary>
/// Minimal <see cref="IMediaStorageProvider"/> test double that records which keys
/// were asked to be deleted, and can be made to throw on delete to exercise the
/// best-effort path. Only the members used by the deletion tests are implemented.
/// </summary>
public sealed class RecordingStorageProvider : IMediaStorageProvider
{
    public List<string> DeletedKeys { get; } = new();

    /// <summary>When set, DeleteAsync throws for any key for which this returns true.</summary>
    public Func<string, bool>? ThrowOnDelete { get; set; }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        if (ThrowOnDelete?.Invoke(storageKey) == true)
            throw new InvalidOperationException($"Simulated storage failure for {storageKey}");

        DeletedKeys.Add(storageKey);
        return Task.CompletedTask;
    }

    // ── Unused by deletion tests ─────────────────────────────────────────────
    public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public Task UploadObjectAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    public bool Exists(string storageKey) => throw new NotImplementedException();
    public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
    public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
