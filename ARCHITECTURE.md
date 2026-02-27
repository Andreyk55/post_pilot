# Post Pilot — Architecture Report

> Updated: 2026-02-27. Reflects the current single-server architecture (no Lambda/SQS/EventBridge).

---

## 1. Architecture Overview

### Components & Responsibilities

| Component | Technology | Responsibility |
|---|---|---|
| **API Server** | .NET 10 / ASP.NET Core (Kestrel) | Handles all HTTP requests (CRUD posts, OAuth, media upload URLs, AI, validation) |
| **PostPublishingWorker** | BackgroundService (in-process) | Polls every 30s, publishes due posts, recovers stuck posts |
| **Frontend** | React 19 + TypeScript + Vite | SPA served separately (localhost:5173 dev), consumes `/api/*` |
| **Database** | PostgreSQL 16 (Docker locally, RDS planned) | Posts, MediaItems, MetaConnections, OAuth state, AI voice profiles |
| **Media Storage** | Local filesystem (dev) / S3 (prod) | Images & videos uploaded by user |
| **AI Services** | Google Gemini API | Caption generation, language detection, media analysis |

### Entry Points

| Entry Point | File | Purpose |
|---|---|---|
| `Program.cs` | `backend/Program.cs` | Application entry point — Kestrel server |
| `Startup.cs` | `backend/Startup.cs` | DI composition root and middleware pipeline |

---

## 2. Runtime Architecture (Request Flow)

### HTTP Request Flow

```
Client (React SPA)
  → CorrelationIdMiddleware
    → CORS ("AllowFrontend")
      → ASP.NET Core Routing
        → Controller (PostsController, MediaController, MetaController, etc.)
          → Services (via DI)
            → Database (EF Core / Npgsql)
            → External APIs (Meta Graph, Gemini)
            → Media storage (S3 / local filesystem)
```

**No auth middleware** — there's no authentication/authorization layer today. All endpoints are open.

### DI Composition Root (`backend/Startup.cs`)

Key registrations:

- **Scheduler**: `LocalPostScheduler` (polling-based, no external triggers)
- **Publishing worker**: `PostPublishingWorker` (BackgroundService, always registered)
- **Media**: `MEDIA_BUCKET_NAME` env var → `S3MediaService` (prod) or `LocalMediaService` (dev)
- **Video frames**: `FFmpegVideoFrameExtractor` (requires ffprobe on PATH)
- **Insights**: `FeatureSettings.EnableEngagementFetch` → `FacebookInsightsService` or `DisabledFacebookInsightsService`

### Background Processing

`PostPublishingWorker` — an `IHostedService` that polls every **30 seconds**:
1. Recovers stuck `Publishing` posts (>5 min threshold)
2. Finds due posts (`Scheduled` + `ScheduledAt <= now`, or `RetryPending`/`Processing` + `NextRetryAt <= now`)
3. Publishes each in-process via platform-specific publishers

### Publishing Pipeline

```
PostPublishingWorker (every 30s)
  → Query: Posts WHERE (Scheduled AND ScheduledAt<=now)
                    OR (RetryPending/Processing AND NextRetryAt<=now)
  → For each due post:
    → Resolve publisher by Platform + PostType
    → Feed: IPostPublisherResolver → FacebookPagePublisher / InstagramPublisher
    → Story: IStoryPublisherResolver → FacebookStoryPublisher / InstagramStoryPublisher
    → On success: Status=Published, ExternalPostId saved
    → On transient error: Status=RetryPending, NextRetryAt = exponential backoff
    → On permanent error: Status=Failed
```

---

## 3. Media Pipeline Deep Dive

### A) Upload Initiation

**Flow**: Frontend → Backend → (S3 or local filesystem)

1. Frontend calls `POST /api/media/upload-url` with `{fileName, contentType}` (`backend/Controllers/MediaController.cs`)
2. Backend returns `{uploadUrl, s3Key, mediaType}`
   - **Dev** (`LocalMediaService`): `uploadUrl = http://localhost:5122/api/media/upload/{guid}.ext`, `s3Key = media/{guid}.ext`
   - **Prod** (`S3MediaService`): `uploadUrl = pre-signed S3 PUT URL` (60 min expiry), `s3Key = media/{guid}.ext`
3. Frontend PUTs the raw file body to the `uploadUrl` using XHR (for progress tracking)

### B) Permanent Storage

| Environment | Storage | Path/Bucket | Naming Scheme |
|---|---|---|---|
| Dev | Local filesystem | `backend/uploads/` | `{guid}.{ext}` (flat) |
| Prod | S3 | `postpilot-media-{env}` | `media/{guid}.{ext}` |

### C) Processing / Validation

| Step | Tool | Where |
|---|---|---|
| Image metadata (dimensions) | ImageSharp | `backend/Services/Validation/ImageMetadataExtractor.cs` |
| Video metadata (duration, codec, fps) | ffprobe CLI | `backend/Services/Validation/FfprobeVideoMetadataExtractor.cs` |
| Platform validation rules | In-code rules per platform/placement | `backend/Services/Validation/MediaValidationRules.cs` |
| Video frame extraction (thumbnails) | FFmpeg CLI | `backend/Services/Ai/FFmpegVideoFrameExtractor.cs` |
| AI image/video analysis | Gemini Vision API | `backend/Services/Ai/MediaAiService.cs` |

### D) Publishing — How Media URLs Are Consumed

Publishers generate **pre-signed download URLs** (or use local URLs) and pass them to Meta's Graph API:

- **Facebook images**: `POST /{pageId}/photos` with `url=<pre-signed GET URL>`
- **Facebook video**: `POST /{pageId}/videos` with `file_url=<pre-signed GET URL>` (2-hour expiry)
- **Facebook multi-photo**: Upload each as unpublished, then create feed post with `attached_media[n]`
- **Instagram**: `POST /{ig-user-id}/media` with `image_url` or `video_url`
- **Local dev**: Uses `PUBLIC_URL` env var (for ngrok tunneling) so Meta can reach the local server

---

## 4. Environment Configuration

### Configuration Sources

| Setting | Dev | Prod | Source |
|---|---|---|---|
| DB connection | `Host=localhost;Port=5432;...` | Env var `DB_CONNECTION_STRING` | `appsettings.Development.json` / env var |
| Meta App ID/Secret | Env vars | Env vars | Always env vars |
| Meta Redirect URI | `http://localhost:5173/oauth/meta/callback` | Env var `META_REDIRECT_URI` | `appsettings.Development.json` / env var |
| Media storage | Local filesystem (`backend/uploads/`) | S3 bucket | `MEDIA_BUCKET_NAME` env var presence |
| Gemini API key/model | Env vars | Env vars | Always env vars |
| CORS | `http://localhost:*` + any `https://` | Same rule | `Startup.cs` |
| Swagger | Enabled | Disabled | `IsDevelopment()` check |
| Public URL (for ngrok) | Env var `PUBLIC_URL` | N/A (S3 URLs used) | `LocalMediaService` |

### Required Environment Variables

| Variable | Purpose |
|---|---|
| `META_APP_ID` | Meta Graph API app ID |
| `META_APP_SECRET` | Meta Graph API app secret |
| `META_REDIRECT_URI` | OAuth callback URL (or via appsettings) |
| `GEMINI_API_KEY` | Google Gemini API key |
| `GEMINI_MODEL` | Gemini model name |
| `DB_CONNECTION_STRING` | PostgreSQL connection (or via appsettings) |
| `MEDIA_BUCKET_NAME` | S3 bucket name (omit for local filesystem) |

---

## 5. AWS Coupling (Remaining)

The only AWS dependency is **S3 for media storage** (optional — local filesystem used when `MEDIA_BUCKET_NAME` is not set):

| Package | Used For |
|---|---|
| `AWSSDK.S3` | `IAmazonS3` in `S3MediaService` |

All other code is provider-agnostic:
- `IPostScheduler` — `LocalPostScheduler` (polling-based)
- `IMediaService` — `LocalMediaService` or `S3MediaService`
- `IPostPublisher` / `IStoryPublisher` — HTTP-based, no AWS dependency
- All controllers, services, middleware, entities, EF Core — standard ASP.NET Core

---

## 6. Known Gaps

| # | Issue |
|---|---|
| 1 | **Media cleanup**: No orphan cleanup exists (uploaded but never attached to a post) |
| 2 | **S3 validation gap**: `S3MediaService.GetLocalFilePath()` returns null — server-side validation doesn't work with S3 |
| 3 | **No auth** — no JWT/API key/session middleware exists |
| 4 | **CORS policy** allows any `https://` origin |
| 5 | **No cleanup of extracted video frames** in `uploads/frames/` |
