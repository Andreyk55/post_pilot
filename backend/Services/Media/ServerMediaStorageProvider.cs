namespace PostPilot.Api.Services.Media;

/// <summary>
/// Stub storage provider for APP_RUN_MODE=server.
/// All methods throw NotImplementedException until a real storage provider is implemented.
/// To add a real provider, implement IMediaStorageProvider and register it in Startup.cs.
/// </summary>
public class ServerMediaStorageProvider : IMediaStorageProvider
{
    private const string NotImplementedMessage =
        "Server media storage provider is not implemented yet. " +
        "Implement IMediaStorageProvider for APP_RUN_MODE=server.";

    public Task<string> CreateUploadUrlAsync(string storageKey, string contentType, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public Task<string> CreateDownloadUrlAsync(string storageKey, TimeSpan expires, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public Task<string?> GetLocalFilePathAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public Task SaveAsync(string storageKey, Stream content, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public bool Exists(string storageKey)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public Task<bool> ObjectExistsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    public Task<StoredObjectInfo?> GetObjectInfoAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }
}
