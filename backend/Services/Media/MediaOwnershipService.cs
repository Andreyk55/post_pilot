using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;

namespace PostPilot.Api.Services.Media;

/// <summary>
/// Single source of truth for "does this storage key belong to this workspace?".
/// Centralizes the ownership predicate so the AI media endpoint (and any other caller)
/// never re-implements the (StorageKey, WorkspaceId) check inconsistently.
///
/// <para>
/// A value is <c>owned</c> only when it is a well-formed, server-issued storage key
/// (never an external URL) AND a <see cref="Entities.Media"/> row for that exact key exists
/// in the given workspace. External URLs (http/https/file/ftp/...), bare hostnames/IPs, and
/// keys belonging to another workspace are all rejected. This is the trust-boundary check
/// that blocks SSRF (server-side fetch of attacker-supplied URLs) and cross-workspace media
/// reads before any bytes are resolved.
/// </para>
/// </summary>
public interface IMediaOwnershipService
{
    /// <summary>
    /// True only when <paramref name="storageKeyOrUrl"/> is a storage key owned by
    /// <paramref name="workspaceId"/>. Any external URL or non-storage value returns false
    /// without touching the database.
    /// </summary>
    Task<bool> IsOwnedStorageKeyAsync(
        string? storageKeyOrUrl,
        Guid workspaceId,
        CancellationToken cancellationToken = default);
}

public sealed class MediaOwnershipService : IMediaOwnershipService
{
    private readonly AppDbContext _db;
    private readonly IMediaService _mediaService;

    public MediaOwnershipService(AppDbContext db, IMediaService mediaService)
    {
        _db = db;
        _mediaService = mediaService;
    }

    public Task<bool> IsOwnedStorageKeyAsync(
        string? storageKeyOrUrl,
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        // Reject anything that isn't a server-issued storage key up front: external URLs
        // (http/https/file/ftp), hostnames/IPs, and empty values all fail IsStorageKey, so
        // they can never trigger a server-side fetch or a cross-workspace storage read.
        if (string.IsNullOrWhiteSpace(storageKeyOrUrl) || !_mediaService.IsStorageKey(storageKeyOrUrl))
            return Task.FromResult(false);

        return _db.Media.AnyAsync(
            m => m.StorageKey == storageKeyOrUrl && m.WorkspaceId == workspaceId,
            cancellationToken);
    }
}
