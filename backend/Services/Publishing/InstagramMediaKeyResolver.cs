using Microsoft.EntityFrameworkCore;
using PostPilot.Api.Data;

namespace PostPilot.Api.Services.Publishing;

/// <summary>
/// Phase 3: resolves which stored object an Instagram publisher should hand to Meta.
///
/// <para>
/// Meta accepts JPEG only for Instagram. When the user uploaded a PNG, the upload-complete
/// flow stored an Instagram-safe JPEG derivative alongside the original. Instagram publishers
/// must publish that derivative, never the raw PNG. JPEG originals (and any non-storage-key
/// media) publish as-is.
/// </para>
///
/// <para>
/// This guarantees the publisher validates and publishes the EXACT same object: the gate
/// guard substitutes the derivative for validation, and this resolver substitutes it for the
/// outbound URL. If a PNG has no derivative, this throws so the publisher fails BEFORE the
/// Meta call with a clear permanent reason (in practice the pre-publish guard already blocks
/// this case; the throw is defense-in-depth).
/// </para>
/// </summary>
internal static class InstagramMediaKeyResolver
{
    /// <summary>
    /// Returns the storage key the Instagram publisher should resolve to a publishing URL.
    /// For a PNG original with a derivative: the derivative key. For JPEG: the original.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The media is a PNG selected for Instagram but no JPEG derivative exists.
    /// </exception>
    public static async Task<string> ResolveAsync(
        AppDbContext db,
        Services.Media.IMediaService mediaService,
        Guid workspaceId,
        string storageKeyOrUrl,
        CancellationToken cancellationToken)
    {
        // Non-storage media (legacy external URLs) is published verbatim.
        if (!mediaService.IsStorageKey(storageKeyOrUrl))
            return storageKeyOrUrl;

        var media = await db.Media
            .AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.StorageKey == storageKeyOrUrl && m.WorkspaceId == workspaceId,
                cancellationToken);

        // No Media row (untracked legacy key): publish as-is; the gate already let it pass.
        if (media == null)
            return storageKeyOrUrl;

        var isPng = string.Equals(media.ContentType, "image/png", StringComparison.OrdinalIgnoreCase);
        if (!isPng)
            return storageKeyOrUrl;

        if (string.IsNullOrEmpty(media.InstagramImageStorageKey))
        {
            // PNG for Instagram without a derivative must never reach Meta.
            throw new InvalidOperationException(
                "PNG image has no Instagram-ready JPEG derivative; cannot publish to Instagram.");
        }

        return media.InstagramImageStorageKey;
    }
}
