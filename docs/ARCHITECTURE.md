# Post Pilot — Architecture Report

> Updated: 2026-05-12. Reflects the current single-server architecture (no Lambda/SQS/EventBridge).

---

## 1. Architecture Overview

### Components & Responsibilities

| Component | Technology | Responsibility |
|---|---|---|
| **API Server** | .NET 10 / ASP.NET Core (Kestrel) | Handles all HTTP requests (CRUD posts, OAuth, media upload/download, AI endpoints, validation) |
| **PostPublishingWorker** | .NET 10 / `Microsoft.NET.Sdk.Worker` (BackgroundService) | Polls DB every 30 s, publishes due posts, recovers stuck posts |
| **Frontend** | React 19 + TypeScript + Vite 7 | SPA served separately (localhost:5173 dev) |
| **Database** | PostgreSQL 16 via EF Core / Npgsql | Single database; EF migrations auto-applied on API startup |
| **Media Storage** | MinIO (S3-compatible) locally; provider-neutral abstraction (`IMediaStorageProvider`) for production | Selected by `MediaStorage:Provider` (`local-disk` or `s3-compatible`). Production provider not yet chosen. |
| **AI** | Google Gemini API | Caption generation, media analysis, language detection |
| **Publishing** | Meta Graph API v21.0 | Facebook Pages/Stories + Instagram Feed/Stories/Carousels |

### Runtime diagram

```
[Frontend SPA]       port 5173 (dev) / static (prod)
     │  HTTP /api/*
     ▼
[PostPilot.Api]      port 5122 — Kestrel, ASP.NET Core
     │  EF Core / Npgsql
     ▼
[PostgreSQL 16]      port 5432

[PostPilot.Publisher]  (no HTTP port, same Docker network)
     │  reads/writes DB every 30 s
     │  calls Meta Graph API v21.0
     ▼
[PostgreSQL 16]

External APIs:
  Meta Graph API  ──►  https://graph.facebook.com/v21.0
  Gemini API      ──►  per GeminiSettings.BaseUrl
```

---

## 2. Runtime Architecture (Request Flow)

### HTTP Request Flow

```
Client (React SPA)
  └─► POST /api/posts          → PostsController.CreatePost
  └─► GET  /api/posts          → PostsController.GetPosts
  └─► GET  /api/meta/...       → MetaController (OAuth, pages, accounts)
  └─► POST /api/media/uploads/init     → MediaController (presigned URL + Media row)
  └─► POST /api/media/uploads/complete → MediaController (HEAD verify, flip to Uploaded)
  └─► GET  /api/media/files/*          → MediaController (streams from configured backend)
  └─► POST /api/ai/text        → AiTextController (caption generation)
  └─► POST /api/ai/media       → AiMediaController (image/video analysis)
```

**No auth middleware** — all endpoints are open; `CurrentUserId` is hardcoded as
`Guid("00000000-0000-0000-0000-000000000001")` in every controller.

### DI Composition Root

`ServiceCollectionExtensions.AddPostPilotCoreServices()` is called by **both** the API
(`backend/Startup.cs`) and the Worker (`backend/publisher/Program.cs`). It registers:

- `AppDbContext` (Npgsql, scoped)
- `AppOptions`, `MetaOptions`, `PublishingOptions`, `MediaOptions`, `FeatureSettings`, `PlatformSelectionOptions`, `MetaApiOptions`, `GeminiSettings`
- `MetaOAuthService` (HttpClient)
- `LocalPostScheduler` (IPostScheduler)
- `FacebookPagePublisher`, `FacebookStoryPublisher`, `InstagramPublisher`, `InstagramStoryPublisher`
- `IPostPublisherResolver`, `IStoryPublisherResolver`
- `MediaService` + `IMediaStorageProvider` (LocalDisk or S3-compatible based on `MediaStorage:Provider`)
- `MediaUploadService` (init/complete flow, Media side-table lifecycle)
- AI services including `GoogleAiClientRouter`, AI rate limiter
- Insights service (feature-flagged, off by default)

---

## 3. Backend Flow

### Post scheduling path

1. `POST /api/posts` → `PostsController.CreatePost`
2. Calls `IPostScheduler.ScheduleAsync` → `LocalPostScheduler` returns `ScheduleResult(true, "local-polling")`
3. `post.ScheduleArn = "local-polling"` ← **AWS leftover field, semantically meaningless now**
4. `post.Status = Scheduled`, saved to DB

### Post publishing path (worker)

1. `PostPublishingWorker` (30 s loop) queries posts where `Status IN (Scheduled, RetryPending)` and time has elapsed
2. Atomically claims post: `UPDATE Posts SET Status='Publishing' WHERE Id=… AND Status='Scheduled'`
3. Creates a new DI scope per post
4. Resolves `IPostPublisherResolver` or `IStoryPublisherResolver`
5. Calls the appropriate publisher:
   - `FacebookPagePublisher` — Facebook Feed posts
   - `FacebookStoryPublisher` — Facebook Stories
   - `InstagramPublisher` — Instagram Feed + Carousels
   - `InstagramStoryPublisher` — Instagram Stories
6. On success → `Status = Published`, `PublishedAt = now`
7. On failure → `RetryCount++`; if < `MaxRetries` (default 3) → `Status = RetryPending`, else → `Status = Failed`
8. Stuck recovery: posts in `Publishing` for > `StuckPostThresholdMinutes` are reset to `Scheduled`

---

## 4. Database

- **Engine**: PostgreSQL 16 via `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.0`
- **Migrations**: 26 migrations, first `20260109…`, latest `20260223215653_AddInstagramMediaTagsJson`
- **Auto-applied**: Yes — `db.Database.MigrateAsync()` runs on API startup only (not in worker)

### Tables

| Table | Entity | Notes |
|---|---|---|
| `Posts` | `Post` | Core entity; contains `ScheduleArn` column (AWS leftover — holds `"local-polling"` today) |
| `PostMediaItems` | `PostMediaItem` | Carousel items (Order 0-based, 2–10 per post) |
| `MetaConnections` | `MetaConnection` | Per-user OAuth token |
| `ConnectedPages` | `ConnectedPage` | Facebook Pages with page-level tokens |
| `ConnectedInstagramAccounts` | `ConnectedInstagramAccount` | IG Business Accounts |
| `MetaOAuthStates` | `MetaOAuthState` | Temporary CSRF state for OAuth flow |
| `AiVoiceProfiles` | `AiVoiceProfile` | Brand voice profiles; soft-delete (`IsDeleted`, `DeletedAt`) |

### Indexes on `Posts`

- `(Status, ScheduledAt)` — pending post lookup
- `(Status, NextRetryAt)` — retry lookup

### Key relations

- `Post` → `ConnectedPage` (SetNull on page delete)
- `Post` → `ConnectedInstagramAccount` (SetNull on account delete)
- `Post` → `PostMediaItems` (Cascade delete)
- `MetaConnection` → `ConnectedPages` / `ConnectedInstagramAccounts` (Cascade delete)

---

## 5. Frontend

- **Stack**: React 19.2, TypeScript 5.9, Vite 7, React Router 7
- **API layer**: Hand-written `fetch` wrappers in `frontend/src/api/`
- **State**: Local React state + `useEffect`; no global state library
- **Config**: `frontend/src/config/appConfig.ts` merges `config/common.json` + `config/{mode}.json` + optional `VITE_API_URL` env override
- **Dev proxy**: Vite proxies `/api/media/files` → `http://localhost:5122` to allow canvas/video operations without CORS

### Config files

| File | Content |
|---|---|
| `frontend/config/common.json` | `{"apiBaseUrl": "http://localhost:5122/api"}` |
| `frontend/config/local.json` | Same as common |
| `frontend/config/prod.json` | **Empty `{}`** — falls back to localhost URL (broken for production) |
| `frontend/config/dev.json` | **Empty `{}`** — falls back to localhost URL |

---

## 6. Media Storage

Backend selected by `MediaStorage:Provider` (bound from `MediaStorage__Provider`):

| Provider | Implementation | Status |
|---|---|---|
| `local-disk` | `LocalDiskMediaStorageProvider` — files under `backend/uploads/` as `{guid}.{ext}`. Default for `dotnet run` without MinIO. | **Working** |
| `s3-compatible` | `S3CompatibleMediaStorageProvider` — uses `AWSSDK.S3` against MinIO locally; works with any S3-compatible backend (S3, R2, Spaces, B2, Wasabi, Hetzner) in the future. | **Working (MinIO)** |
| (production specific provider) | `ServerMediaStorageProvider` stub | **Not implemented** — choose an `s3-compatible` endpoint in prod. |

### Upload flow (new, recommended)

1. `POST /api/media/uploads/init` — creates a `Media` row (status `PendingUpload`), returns `{ mediaId, storageKey, uploadUrl, method: "PUT", expiresAt, contentType }`. The `uploadUrl` is a presigned PUT against the **public** endpoint (`http://localhost:9000` locally).
2. Browser `PUT`s bytes to `uploadUrl` directly (no API in the data path).
3. `POST /api/media/uploads/complete` — API does a HEAD against the **internal** endpoint (`http://minio:9000`), captures `SizeBytes`, flips the row to `Uploaded`. Idempotent.
4. `DELETE /api/media/{mediaId}` — marks row `Deleted` and best-effort removes the object.

The legacy `POST /api/media/upload-url` and `PUT /api/media/upload/{filename}` endpoints are kept marked `[Obsolete]` so frontend rollback stays safe; they will be removed in a later pass.

### File serving / publishing URL

- `GET /api/media/files/{*storageKey}` — catch-all route preserving the full key (e.g. `media/{guid}.jpg`). The API streams from whatever provider is configured (`OpenReadAsync`).
- `IMediaService.GetPublishingUrl(storageKey)` returns `{App.PublicUrl}/api/media/files/{escaped-storageKey}` and is what publishers hand to Meta. The shape is provider-independent — switching object stores doesn't change the public URL Meta sees.

### Two-S3-client trick (S3CompatibleMediaStorageProvider)

The provider holds two `AmazonS3Client` instances:
- `_internalClient` with `ServiceURL = MediaStorage:InternalEndpoint` (e.g. `http://minio:9000`) — used for `HeadObject`, `GetObject`, `DeleteObject`, `PutObject`.
- `_publicClient` with `ServiceURL = MediaStorage:PublicUploadEndpoint` (e.g. `http://localhost:9000`) — used **only** for `GetPreSignedURL`.

S3 signature v4 binds the signature to the endpoint, so a URL signed against `minio:9000` would not validate when the browser hits `localhost:9000`. Two clients sidestep this without proxying through the API.

### Media DB side-table

A new `Media` table tracks upload lifecycle (`PendingUpload` / `Uploaded` / `Deleted`) but is **not** referenced by `Post` or `PostMediaItem`. Posts continue to store storage keys directly in `MediaUrl`. The Media table is an audit ledger, not part of the post→media FK chain.

### Other

- Allowed types: `image/jpeg`, `image/png`, `image/gif`, `video/mp4`.
- Frames endpoint (`/api/media/frames/{filename}`) still serves from local disk inside the API container; not migrated yet.
- Image processing: `SixLabors.ImageSharp 3.1.7`.

---

## 7. Configuration

### Config loading order (both API and Worker processes)

1. `appsettings.json`
2. `appsettings.{ASPNETCORE_ENVIRONMENT}.json`
3. `config/appsettings.common.json` (deployed into both containers via Dockerfile)
4. `config/appsettings.{appEnvironment}.json` (local / dev / prod)
5. Standard env vars — **must use double-underscore notation** e.g. `Gemini__Model`
6. `EnvVarMapper` — flat-name overrides for exactly 4 hardcoded keys

### `EnvVarMapper` — only these 4 flat names are handled

| Flat env var | Maps to config key |
|---|---|
| `APP_RUN_MODE` | `App:RunMode` |
| `META_APP_ID` | `Meta:AppId` |
| `META_APP_SECRET` | `Meta:AppSecret` |
| `GEMINI_API_KEY` | `Gemini:ApiKey` |

All other config must use `__` notation (`App__PublicUrl`, `Gemini__Model`, `Gemini__VisionModel`, etc.).

### Key config values (from `appsettings.common.json`)

| Key | Default value |
|---|---|
| `Gemini:Model` | `gemini-2.0-flash` |
| `Publishing:WorkerPollIntervalSeconds` | `30` |
| `Media:LocalServerBaseUrl` | `http://localhost:5122` |
| `Features:EnableEngagementFetch` | `false` |
| `Ai:RateLimiter:MaxCallsPerDay` | `20` |

### Validators (run at startup, will crash if invalid)

- `AppOptionsValidator` — `RunMode` must be `"local"` or `"server"`, `PublicUrl` must be absolute URI if set
- `MetaOptionsValidator` — `AppId`, `AppSecret`, `RedirectUri` required
- `GeminiSettingsValidator` — `ApiKey`, `Model`, `BaseUrl` required; `TimeoutSeconds > 0`

---

## 8. Docker Setup

### Dockerfile — 3 stages

| Stage | Base image | Output |
|---|---|---|
| `build` | `dotnet/sdk:10.0` | Restores/compiles both projects |
| `api` | `dotnet/aspnet:10.0` | Published API + `ConfigurationFiles/` → `./config/`; exposes port 5122; creates `/app/uploads` |
| `publisher` | `dotnet/aspnet:10.0` | Published worker + `ConfigurationFiles/` → `./config/` |

### docker-compose.yml — app stack

- `api` service: target `api`, port `5122:5122`, `restart: unless-stopped`, `env_file: ${ENV_FILE}`
- `publisher` service: target `publisher`, no ports, `depends_on: api`, `env_file: ${ENV_FILE}`
- Media is now stored in MinIO when `MediaStorage__Provider=s3-compatible` — no `/app/uploads` volume is needed for that path. The legacy local-disk path still writes to `/app/uploads` inside the container and remains ephemeral.

### docker-compose.local.db.yml — database stack

- `postgres:16`: port `5432`, persistent named volume `postgres_data`
- `pgadmin4`: port `5050`
- Defaults to `env/local.env` if `$ENV_FILE` is unset

### docker-compose.local.storage.yml — object storage stack

- `minio`: `minio/minio` image, ports `9000` (API) and `9001` (console), persistent named volume `minio_data`, healthcheck on `/minio/health/live`. CORS allow-origin is set to `http://localhost:5173,http://localhost:5122` so the browser can `PUT` directly.
- `minio-init`: one-shot `minio/mc` container that runs `mc mb --ignore-existing local/postpilot-media` and exits. `depends_on: minio (service_healthy)`.

### docker-compose.local.depends.yml — service start ordering

- Adds `depends_on` overrides to `api` and `publisher`: wait for `postgres` healthy and `minio-init` completed; `publisher` additionally waits for `api` to start. Kept in its own overlay so `local.db.yml` and `local.storage.yml` can be loaded standalone (e.g. `pgadmin-start.ps1`) without referencing api/publisher.

---

## 9. External Integrations

| Integration | Usage | Config |
|---|---|---|
| **Meta Graph API v21.0** | OAuth flow, publish Facebook Feed/Stories, Instagram Feed/Stories/Carousels | `Meta:AppId`, `Meta:AppSecret`, `Meta:RedirectUri`; URL hardcoded in `MetaApiOptions.cs` |
| **Google Gemini API** | Caption generation, language detection, image/video analysis | `Gemini:ApiKey`, `Gemini:Model`, `Gemini:VisionModel`; routed via `GoogleAiClientRouter` |
| **ngrok** (local only) | Provides a public `App:PublicUrl` so Meta can reach local media | Set via `App__PublicUrl` in `local.env` |

---

## 10. Remaining AWS / Provider-Specific Leftovers

| Location | Leftover | Impact |
|---|---|---|
| `backend/Entities/Post.cs` | `public string? ScheduleArn` field | DB column exists; written with literal `"local-polling"` string today |
| `backend/Controllers/PostsController.cs` (two call sites) | `post.ScheduleArn = scheduleResult.ScheduleIdentifier` | Harmless but misleading |
| All migration `*.Designer.cs` + snapshot | `ScheduleArn` column modeled | Must be dropped via a new migration |
| `backend/Controllers/InternalController.cs` | Comment: *"In production, these are called by EventBridge via Lambda"* | Stale — worker calls publishers directly |
| `backend/Controllers/AiMediaController.cs` | Comments referencing Lambda/FFmpeg approach | Stale |
| Test data | `ScheduleArn = "arn:aws:scheduler:test"` | Leftover test value |
| `backend/ConfigurationFiles/appsettings.common.json` | Empty `Aws` section | Should be removed |

---

## 11. Problems / Risks Found

### Critical

1. **Credentials in `dev/local.env`**
   Real-looking `META_APP_ID`, `META_APP_SECRET`, `GEMINI_API_KEY`, and ngrok URL are committed in plaintext. Rotate these credentials and replace the file with a placeholder template.

2. **No authentication on any endpoint**
   `CurrentUserId = Guid.Parse("00000000-0000-0000-0000-000000000001")` is hardcoded in `AiTextController`, `AiMediaController`, `MetaController`, `AiVoiceProfileController`. Any caller can access all data.

3. **Production storage provider intentionally unimplemented**
   `MediaStorage:Provider=s3-compatible` covers MinIO locally and works against any S3-compatible production backend once one is chosen (Cloudflare R2, DigitalOcean Spaces, Backblaze B2, Hetzner Object Storage, Wasabi, AWS S3). `ServerMediaStorageProvider` remains a deliberate stub — setting `APP_RUN_MODE=server` will crash on media operations, but `APP_RUN_MODE` no longer selects the storage backend (`MediaStorage:Provider` does).

### High

4. **`App:PublicUrl` not set in server mode**
   `server.env` uses `PUBLIC_URL=…` which is not a recognized flat var and is not mapped. In server mode, `App:PublicUrl` is empty — media URLs resolve to `http://localhost:5122/…` and are unreachable for Meta to serve from.

5. **`GEMINI_MODEL` / `GEMINI_VISION_MODEL` flat vars silently ignored**
   Both env files set `GEMINI_MODEL=…` but `EnvVarMapper` does not map this name, and ASP.NET Core env var provider requires `Gemini__Model` notation. Model always falls back to `appsettings.common.json` value (`gemini-2.0-flash`).

6. **Local-disk media path is still ephemeral**
   With `MediaStorage__Provider=s3-compatible`, media lives in MinIO with a persistent named volume — restart-safe. With `MediaStorage__Provider=local-disk`, files still write to `/app/uploads` inside the API container with no volume mount, and remain ephemeral. Use the s3-compatible path for any flow where the media must survive a container restart.

### Medium

7. **Frontend production config is empty**
   `frontend/config/prod.json` is `{}`, so production builds use `apiBaseUrl: http://localhost:5122/api`. Production deployments need either a populated `prod.json` or `VITE_API_URL` build arg.

8. **`ENV_FILE` has no default in `docker-compose.yml`**
   `env_file: - ${ENV_FILE}` will fail compose if `ENV_FILE` is not set in the shell. The DB compose correctly defaults to `env/local.env`; the app compose does not.

9. **`GEMINI_VISION_MODEL=REPLACE_ME` not caught by validator**
   Because the flat name is unmapped, this value never reaches `GeminiSettings.VisionModel`. The `PostConfigure` fallback sets VisionModel to Model when null/empty — so it accidentally works today. If mapping were fixed, `"REPLACE_ME"` would be used as the actual model name.

10. **`Post.ScheduleArn` is a dead DB column**
    Written with `"local-polling"` string. Should be removed via a migration.

---

## 12. Recommended Next Steps

### Immediate (security / correctness)

- Rotate credentials from `dev/local.env`; replace with a redacted template (like `prod/server.env.example`); ensure the file is gitignored
- Fix `server.env`: rename `PUBLIC_URL` → `App__PublicUrl` so ASP.NET Core picks it up
- Fix `GEMINI_MODEL` / `GEMINI_VISION_MODEL` in both env files: rename to `Gemini__Model` / `Gemini__VisionModel`
- Add a named volume for `/app/uploads` to `docker-compose.yml`

### Soon (functional correctness)

- Add `:-env/local.env` default to `ENV_FILE` in `docker-compose.yml`
- Populate `frontend/config/prod.json` with the production API URL
- Write a migration to drop the `ScheduleArn` column from `Posts`

### Before any multi-user or production use

- Implement authentication (JWT / API key) and replace all hardcoded `CurrentUserId` references
- Implement `ServerMediaStorageProvider` (S3 or Azure Blob) before using `APP_RUN_MODE=server`

### Cleanup

- Remove stale EventBridge/Lambda comments in `InternalController.cs` and `AiMediaController.cs`
- Remove the empty `Aws` config section from `appsettings.common.json`
- Remove the `ScheduleArn` test data string from test files
