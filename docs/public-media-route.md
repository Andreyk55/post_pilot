# Public media route — `GET /api/media/files/{*storageKey}`

This is the only `[AllowAnonymous]` data-serving endpoint in the API. It must
stay anonymous for current publishing flows to work. This doc explains why,
what the risk surface is, and the path to a hardened replacement.

## What it does

Streams a stored media file by its raw storage key. The route is a catch-all
(`{*storageKey}`) so keys like `media/{guid}.jpg` survive intact end-to-end.
The handler lives in [backend/Controllers/MediaController.cs](../backend/Controllers/MediaController.cs)
under `GetFile`.

## Why it must stay public

At publish time the API hands the URL of a media file to Meta:

```
{App.PublicUrl}/api/media/files/media/{guid}.jpg
```

Meta's Graph API fetchers (Facebook, Instagram) then `GET` that URL from
their own infrastructure to ingest the image or video. Those fetchers do
**not** present any auth — no cookie, no token, no header. If we add
`[Authorize]` to this route, every Facebook/Instagram publish breaks.

The same key is also embedded in image/video previews in the SPA. The SPA
**is** authenticated, but it requests the file via the same anonymous route
because that's where Meta will pull it from.

## Risk surface

What an attacker would need to access an arbitrary file:

1. **Knowledge of the storage key.** Keys are produced server-side as
   `media/{Guid.NewGuid():N}.{ext}`. A v4 GUID is 122 bits of entropy.
2. **No enumeration endpoint exists.** The API never returns a list of keys.
3. **Keys are not logged in user-facing surfaces.** They appear in publish-time
   logs (server-only) and inside the publish payload sent to Meta.

So in practice this is a "capability URL" — anyone holding the URL can fetch
the bytes. The bytes are always media the user intended to publish to public
social networks; once published, the same media is also reachable through
Meta's CDN with no auth. The marginal exposure of the public route is small.

## What is not OK

- **Treating storage keys as secret data**: they aren't. Don't log them in
  client-visible places. Don't put them in error messages. Don't put them in
  URLs that get cached in browser histories outside the SPA itself.
- **Adding paths that return all keys**: even a paginated listing endpoint
  would weaken this from "capability URL" to "enumerable".
- **Reusing the same key across workspaces**: the per-workspace `Media` row is
  what enforces ownership for the *authenticated* media endpoints
  (`/uploads/init`, `/uploads/complete`, `DELETE /media/{id}`, `validate`,
  `extract-metadata`). The unauth `GetFile` is intentionally workspace-blind.

## Future hardening

Two non-breaking paths exist if we want to drop the anonymous surface:

1. **Short-lived presigned URLs per publish**. At publish time, generate a
   presigned `GET` URL (signed by the object store) with a short expiry,
   hand *that* URL to Meta, and let the publish complete before it expires.
   Drop `[AllowAnonymous]` on `/api/media/files/{key}`. Cost: media URLs in
   posts become un-cacheable after expiry; we need to refresh them in the
   SPA's image components.
2. **Split routes**. Keep an anonymous `/api/media/public/{key}` for Meta
   (same behaviour as today), and add an authenticated `/api/media/files/{key}`
   for SPA use. The SPA stops sharing URLs with Meta, which means publishing
   uses the public route and previewing uses the auth'd one. Cost: the
   anonymous route remains, but with a smaller blast radius (only files
   actively being published).

Both options need work in `MediaService` / the storage providers
(`S3CompatibleMediaStorageProvider`, `LocalDiskMediaStorageProvider`) to mint
the presigned URLs, and in the publishers
(`FacebookPagePublisher`, `InstagramPublisher`, story publishers) to use them.
That is a deliberate follow-up, not a hot-fix.

## Related code

- [`backend/Controllers/MediaController.cs:GetFile`](../backend/Controllers/MediaController.cs) — the route itself, with an
  inline doc-comment that mirrors this note.
- [`backend/Services/Media/IMediaStorageProvider.cs`](../backend/Services/Media/IMediaStorageProvider.cs) — where presigned-URL
  generation would land.
- [`backend/Services/Publishing/`](../backend/Services/Publishing/) — every publisher that currently embeds the public URL.
