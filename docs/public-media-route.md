# Media file access — `GET /api/media/{mediaId}/file`

> **Update (media privacy redesign):** the anonymous catch-all route this document
> originally covered — `GET /api/media/files/{*storageKey}` (`MediaController.GetFile`) —
> has been **removed**. Media file access is now exclusively through the authenticated,
> workspace-scoped route described below. This document keeps the historical rationale at
> the bottom for context on why the old route existed and what changed.

## Current design

`GET /api/media/{mediaId}/file` (`MediaController.GetMediaFile`):

- Requires the normal authenticated app session (`[Authorize]` on the controller — no
  `[AllowAnonymous]` on this action).
- Resolves the caller's current workspace server-side via `ICurrentWorkspaceProvider`
  (never trusts a workspace id from the client).
- Looks up the `Media` row by `mediaId` **and** `WorkspaceId`; a missing row and a
  foreign-workspace row both return the same 404 (`{"error":"Media not found"}`) so the
  response never discloses which case it was.
- Streams bytes via the storage provider using `Media.StorageKey` internally — the
  frontend never learns the StorageKey, only the `mediaId` (and this URL).
- `?variant=thumbnail` serves the derived thumbnail (`Media.ThumbnailStorageKey`) instead
  of the original asset.
- Applies the same safe response headers as before: `X-Content-Type-Options: nosniff`,
  the real image/video content type for known-renderable types, and
  `application/octet-stream` + `Content-Disposition: attachment` for anything else.

The frontend only ever holds `mediaId` (returned by `/uploads/init`, `/uploads/complete`,
and every post/media response) and builds this URL from it — it never sees a raw
StorageKey or a `/api/media/files/...` path.

## Why removing the anonymous route did not break publishing

At publish time the worker/publishers ask `IMediaService.GetPublishingUrlAsync` for the
URL to hand Meta. For the production storage backends (Supabase, S3-compatible) this
already mints a **short-lived signed URL directly from the object store** — the old
anonymous proxy route was never in that path. It was only used:

1. In local-disk dev mode (no object store to sign against), and
2. As a rare fallback if signing an object-store URL throws.

Both of those fallback paths now produce a URL that 404s (the route is gone). This is an
accepted, deliberate trade-off of the redesign — the production critical path (Supabase
signed URLs) is unaffected — but local-disk-mode Meta publishing and the signing-failure
fallback are known follow-up items if they need to keep working; see the doc comments on
`MediaService.GetPublishingUrlAsync`.

## Related code

- [`backend/Controllers/MediaController.cs`](../backend/Controllers/MediaController.cs) — `GetMediaFile` (current route) and `GetOwnedMediaAsync`/`BuildMediaFileUrl` helpers.
- [`backend/Services/Media/IMediaService.cs`](../backend/Services/Media/IMediaService.cs) — `GetPublishingUrlAsync`, which mints the signed URL handed to Meta at publish time.
- [`backend/Services/Publishing/`](../backend/Services/Publishing/) — the publishers that call `GetPublishingUrlAsync`.
- [`backend/backend.Tests/Controllers/MediaPublicFetchTests.cs`](../backend/backend.Tests/Controllers/MediaPublicFetchTests.cs) — pins that the old route no longer exists and that the new one is workspace-scoped.

## Historical context (why the old route was anonymous)

The removed route was a "capability URL": streamed a stored file by its raw storage key,
with a catch-all path so multi-segment keys survived intact. It stayed anonymous because
Meta's Facebook/Instagram fetchers pulled bytes from it directly during publishing and
presented no auth. Storage keys carried a high-entropy `Guid` mediaId, there was no
enumeration endpoint, and publishers redacted keys/URLs from logs — so the residual risk
was considered acceptable for the MVP. The mediaId-based authenticated route above removes
that residual risk entirely for the frontend-facing surface, and production publishing was
already bypassing the anonymous route via signed object-store URLs.

