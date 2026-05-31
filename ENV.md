# Environment Variables

## Required (env vars only — never in appsettings)

| Variable | Purpose |
|---|---|
| `App__RunMode` | `local` or `server` — controls media storage strategy |
| `Meta__AppId` | Facebook/Instagram OAuth app ID |
| `Meta__AppSecret` | Facebook/Instagram OAuth app secret |
| `Gemini__ApiKey` | Google Gemini AI API key |
| `GoogleAuth__ClientId` | Google OAuth 2.0 client id (real-user login) |
| `GoogleAuth__ClientSecret` | Google OAuth 2.0 client secret (real-user login) |

Legacy flat names (`APP_RUN_MODE`, `META_APP_ID`, `META_APP_SECRET`, `GEMINI_API_KEY`)
are still accepted for backward compatibility.

## Media storage (production: Supabase Storage)

Backend and worker only. NEVER set these in any Vercel/frontend build.

| Variable | Required | Purpose |
|---|---|---|
| `MediaStorage__Provider` | yes (prod) | `Supabase` in production. `s3-compatible` is kept for rollback to MinIO/S3/R2; `local-disk` is dev-only and rejected when `App__RunMode=Server`. |
| `MediaStorage__Supabase__Url` | yes (when Supabase) | Project URL, e.g. `https://YOUR_PROJECT.supabase.co`. |
| `MediaStorage__Supabase__ServiceRoleKey` | yes (when Supabase) | Service-role JWT. Bypasses RLS — treat like a DB password. Backend/worker only. |
| `MediaStorage__Supabase__Bucket` | yes (when Supabase) | Must already exist and be **private**. Default: `postpilot-media`. |
| `MediaStorage__Supabase__SignedUrlExpirySeconds` | optional | Lifetime of signed upload/download URLs handed to the browser and Meta. Default `3600` (1h). |
| `MediaStorage__Supabase__MaxUploadBytes` | optional | Hard ceiling enforced at `/api/media/uploads/init`. `0` = no extra cap. |

The Supabase service-role key MUST NOT be exposed to the frontend — it grants
full project access. Configure it in `/opt/postpilot/server.env` on the VPS (or
the GitHub Actions environment) and verify it does not appear in any
`VITE_*` / `NEXT_PUBLIC_*` / Vercel project variable.

### Storage key layout

The backend (never the frontend) builds object keys in this shape:

```
users/{userId}/workspaces/{workspaceId}/providers/{providerPlatform}/media/{mediaId}/{safeFileName}
```

`{userId}` is the authenticated PostPilot app user id (never an email, display
name, Meta account id, Facebook page id, or provider user id). Before minting an
upload/read signed URL the backend verifies the authenticated user has access to
`{workspaceId}` (membership re-checked in the DB), and the resolved user id — not
anything the client supplies — is what fills the `users/{userId}` segment.

MVP rule: each upload belongs to one publishing platform only — no cross-posting
yet, so the path carries a single deterministic platform segment.

Existing media uploaded under the older `workspaces/{workspaceId}/…` layout is
not migrated; only new uploads use the `users/{userId}/…` prefix.

`providerPlatform` allow-list (mapped server-side from the typed `Platform` enum
the client sends in the init request):

| Platform value | providerPlatform segment |
|---|---|
| `Facebook`  | `meta-facebook`  |
| `Instagram` | `meta-instagram` |

Any other platform value is rejected. The frontend MUST send only file metadata
(name, content type, size) plus the typed `Platform` value — never a storage
key or path.

## Real-user auth (Auth section)

| Variable | Required | Purpose |
|---|---|---|
| `Auth__FrontendUrl` | yes (prod) | Origin the backend redirects to after Google login, e.g. `https://app.example.com`. In dev defaults to `http://localhost:5173` via `appsettings.Development.json`. |
| `Auth__AllowedOrigins__0`, `__1`, … | yes (prod) | Full **origins** (scheme + host + optional port, no trailing slash) permitted by CORS and as logout-Origin checks. Examples: `https://app.example.com`, `https://postpilot.vercel.app`. Bare domains, wildcards, and paths are not supported. Localhost dev origins are always allowed in addition. |
| `Auth__CookieDomain` | optional | Set when API and frontend share a parent domain (e.g. `.example.com`). Leave empty for cross-site setups. |
| `Auth__RequireHttpsCookies` | recommended `true` in prod | When true the session cookie is `Secure` + `SameSite=None`. Set false only for local HTTP dev. |
| `Auth__CookieName` | optional | Defaults to `postpilot_session`. |

## Configuration (appsettings, not env vars)

| Key | Default | Location |
|---|---|---|
| `App:PublicUrl` | (empty) | `config/appsettings.common.json` |
| `Gemini:Model` | `gemini-2.0-flash` | `config/appsettings.common.json` |
| `Gemini:VisionModel` | Falls back to `Gemini:Model` | (code fallback) |
| `Meta:RedirectUri` | `http://localhost:5173/oauth/meta/callback` | `appsettings.Development.json` |

These can be overridden with `__`-separated env vars if needed
(e.g. `Gemini__Model`), but are not required as env vars.

## Framework

| Variable | Default | Purpose |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Controls which `appsettings.{env}.json` loads |
| `ConnectionStrings__DefaultConnection` | — | PostgreSQL connection string |

## Frontend (build-time only)

| Variable | Default | Purpose |
|---|---|---|
| `VITE_API_URL` | `http://localhost:5122/api` | Backend API base URL |

## Security

- `Gemini__ApiKey` is sent via `x-goog-api-key` header (never in URL query strings)
- Sensitive headers (`x-goog-api-key`, `Authorization`) are not logged
- Backend secrets must never be exposed to the frontend
- `GoogleAuth__ClientSecret` is only used on the backend during the OAuth code exchange. Google access tokens are never persisted or logged.
- The session cookie (`postpilot_session`) is `HttpOnly` and signed by ASP.NET data protection — never read it from JavaScript or stuff a JWT into localStorage.
