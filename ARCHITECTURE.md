# Post Pilot — As-Is Architecture Report

> Generated: 2026-02-27. This documents the **current state** of the codebase, not aspirational design.

---

## 1. Architecture Overview

### Components & Responsibilities

| Component | Technology | Responsibility |
|---|---|---|
| **API Lambda** | .NET 10 / ASP.NET Core behind API Gateway | Handles all HTTP requests (CRUD posts, OAuth, media upload URLs, AI, validation) |
| **Dispatcher Lambda** | .NET 10 standalone Lambda | Runs every 1 min via EventBridge, queries due posts, sends to SQS |
| **Publisher Lambda** | .NET 10 standalone Lambda | Triggered by SQS, publishes 1 post to Meta (Facebook/Instagram) |
| **Stuck Recovery Lambda** | .NET 10 standalone Lambda | Runs every 5 min, recovers posts stuck in `Publishing` >10 min |
| **Frontend** | React 19 + TypeScript + Vite | SPA served separately (localhost:5173 dev), consumes `/api/*` |
| **Database** | PostgreSQL 16 (Docker locally, RDS planned) | Posts, MediaItems, MetaConnections, OAuth state, AI voice profiles |
| **Media Storage** | Local filesystem (dev) / S3 (prod) | Images & videos uploaded by user |
| **AI Services** | Google Gemini API | Caption generation, language detection, media analysis |

### Entry Points

| Entry Point | File | When Used |
|---|---|---|
| `Program.cs` | `backend/Program.cs` | Local development (`dotnet run`) — standard Kestrel server |
| `LambdaEntryPoint.cs` | `backend/LambdaEntryPoint.cs` | AWS Lambda — extends `APIGatewayHttpApiV2ProxyFunction` |
| `Startup.cs` | `backend/Startup.cs` | Shared composition root (used by both above) |
| `LambdaStartup.cs` | `backend/Lambdas/LambdaStartup.cs` | Separate DI root for Dispatcher/Publisher/StuckRecovery Lambdas |
| `DispatcherFunction.FunctionHandler` | `backend/Lambdas/DispatcherFunction.cs` | EventBridge → Lambda |
| `PublisherFunction.FunctionHandler` | `backend/Lambdas/PublisherFunction.cs` | SQS → Lambda |
| `StuckRecoveryFunction.FunctionHandler` | `backend/Lambdas/StuckRecoveryFunction.cs` | EventBridge → Lambda |

---

## 2. Runtime Architecture (Request Flow)

### HTTP Request Flow (API Lambda / Kestrel)

```
Client (React SPA)
  → API Gateway (prod) / direct HTTP (dev)
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

### DI Composition Root (`backend/Startup.cs:27-131`)

Key registrations with environment-conditional logic:

- **Scheduler**: `SCHEDULER_TYPE` env var → `EventBridgePostScheduler` (prod) or `LocalPostScheduler` + `LocalSchedulerBackgroundService` (dev)
- **Media**: `MEDIA_BUCKET_NAME` env var → `S3MediaService` (prod) or `LocalMediaService` (dev)
- **Video frames**: `LAMBDA_TASK_ROOT` env var → `NoOpVideoFrameExtractor` (Lambda) or `FFmpegVideoFrameExtractor` (dev)
- **Insights**: `FeatureSettings.EnableEngagementFetch` → `FacebookInsightsService` or `DisabledFacebookInsightsService`

### Background Processing

**Local dev only**: `LocalSchedulerBackgroundService` — an `IHostedService` that polls every **30 seconds**:
1. Recovers stuck `Publishing` posts (>5 min threshold)
2. Finds due posts (`Scheduled` + `ScheduledAt <= now`, or `RetryPending`/`Processing` + `NextRetryAt <= now`)
3. Publishes each in-process (no SQS involved)

**Production**: This background service is NOT registered (the `SCHEDULER_TYPE=EventBridge` path skips `AddHostedService`). Instead:
- EventBridge (1 min) → Dispatcher Lambda → SQS → Publisher Lambda
- EventBridge (5 min) → StuckRecovery Lambda

### Publishing Pipeline (Production)

```
EventBridge (1 min cron)
  → DispatcherFunction
    → Query: Posts WHERE (Scheduled AND ScheduledAt<=now)
                      OR (RetryPending/Processing AND NextRetryAt<=now)
    → Atomic claim: SET Status=Publishing (optimistic concurrency via WHERE Status=original)
    → Send to SQS FIFO queue (MessageGroupId=PostId)
      → PublisherFunction
        → Resolve publisher by Platform + PostType
        → Feed: IPostPublisherResolver → FacebookPagePublisher / InstagramPublisher
        → Story: IStoryPublisherResolver → FacebookStoryPublisher / InstagramStoryPublisher
        → On success: Status=Published, ExternalPostId saved
        → On transient error: Status=RetryPending, NextRetryAt = exponential backoff (2/4/8 min)
        → On permanent error: Status=Failed
```

---

## 3. Media Pipeline Deep Dive

### A) Upload Initiation

**Flow**: Frontend → Backend → (S3 or local filesystem)

1. Frontend calls `POST /api/media/upload-url` with `{fileName, contentType}` (`backend/Controllers/MediaController.cs:31`)
2. Backend returns `{uploadUrl, s3Key, mediaType}`
   - **Dev** (`backend/Services/Media/LocalMediaService.cs:57-89`): `uploadUrl = http://localhost:5122/api/media/upload/{guid}.ext`, `s3Key = media/{guid}.ext`
   - **Prod** (`backend/Services/Media/S3MediaService.cs:52-90`): `uploadUrl = pre-signed S3 PUT URL` (60 min expiry), `s3Key = media/{guid}.ext`
3. Frontend PUTs the raw file body to the `uploadUrl` using XHR (for progress tracking) (`frontend/src/api/media.ts:142-180`)
   - **Dev**: File goes to `PUT /api/media/upload/{filename}` → saved to `backend/uploads/{filename}` (`backend/Controllers/MediaController.cs:78-112`)
   - **Prod**: File goes directly to S3 (no backend involvement)

### B) Temporary Storage

- **No temp storage exists**. Files go directly to permanent storage (local `uploads/` or S3).
- Video frame extraction (for AI thumbnails) creates files in `backend/uploads/frames/` (dev only, via FFmpeg). These are **never cleaned up**.
- The `AssetResolver` (`backend/Services/Ai/AssetResolver.cs:60-83`) reads files into memory (`byte[]`) for AI analysis — no temp files, pure in-memory.

### C) Permanent Storage

| Environment | Storage | Path/Bucket | Naming Scheme |
|---|---|---|---|
| Dev | Local filesystem | `backend/uploads/` | `{guid}.{ext}` (flat) |
| Prod | S3 | `postpilot-media-{env}` | `media/{guid}.{ext}` |

- The `s3Key` (e.g. `media/abc123.jpg`) is stored in `Post.MediaUrl` or `PostMediaItem.MediaUrl` in the database.
- **No separate "original" vs "processed" copies** — the uploaded file is the only copy.
- S3 bucket has **public access blocked**; all reads use pre-signed GET URLs.

### D) Processing / Validation

| Step | Tool | Where |
|---|---|---|
| Image metadata (dimensions) | ImageSharp | `backend/Services/Validation/ImageMetadataExtractor.cs` — reads from local file path |
| Video metadata (duration, codec, fps) | ffprobe CLI | `backend/Services/Validation/FfprobeVideoMetadataExtractor.cs` — requires ffprobe on PATH |
| Platform validation rules | In-code rules per platform/placement | `backend/Services/Validation/MediaValidationRules.cs` |
| Video frame extraction (thumbnails) | FFmpeg CLI (dev) / No-op (Lambda) | `backend/Services/Ai/FFmpegVideoFrameExtractor.cs` |
| AI image/video analysis | Gemini Vision API | `backend/Services/Ai/MediaAiService.cs` |

**Critical limitation**: `S3MediaService.GetLocalFilePath()` returns `null` — server-side validation and metadata extraction **do not work in production** (`backend/Services/Media/S3MediaService.cs:154-163`). The TODO comment says "implement async download to temp file."

### E) Publishing — How Media URLs Are Consumed

Publishers generate **pre-signed download URLs** (or use local URLs) and pass them to Meta's Graph API:

- **Facebook images**: `POST /{pageId}/photos` with `url=<pre-signed GET URL>` — Meta fetches the image
- **Facebook videos**: `POST /{pageId}/videos` with `file_url=<pre-signed GET URL>` (2-hour expiry for video processing time)
- **Facebook multi-photo**: Upload each as unpublished (`published=false`), then create feed post with `attached_media[n]` referencing photo IDs
- **Instagram**: `POST /{ig-user-id}/media` with `image_url` or `video_url` — same pattern
- **Local dev special case**: Uses `PUBLIC_URL` env var (for ngrok tunneling) so Meta can reach the local server

**Expiry considerations**:
- Image download URLs: 1 hour (`MetaDownloadUrlExpiration`)
- Video download URLs: 2 hours (longer for processing)
- S3 upload URLs: 60 minutes (`UploadUrlExpiration`)
- Asset resolver URLs: 15 minutes

### F) Cleanup & Retention

**There is NO cleanup mechanism.** Specifically:

- **No TTL / lifecycle policy** on S3 objects
- **No scheduled cleanup** of orphaned media (uploaded but never attached to a post)
- **No cleanup on post deletion** — deleting a post removes the DB row + media items (cascade delete on `PostMediaItem`), but the S3 object / local file **remains forever**
- **No cleanup of extracted video frames** in `uploads/frames/`
- **No cleanup of failed/abandoned uploads** (user starts upload, never creates post)

### G) Limits, Timeouts, and Failure Modes

| Limit | Value | Source |
|---|---|---|
| Max image size | 20 MB | `LocalMediaService.cs:29` |
| Max video size | 200 MB | `LocalMediaService.cs:30` |
| Upload request body limit | 250 MB | `MediaController.cs:77` |
| S3 upload URL expiry | 60 min | `S3MediaService.cs:32` |
| Lambda timeout (API) | 30 sec | `template.yaml:40` |
| Lambda timeout (Publisher) | 60 sec | `template.yaml:193` |
| IG video poll max | 20 attempts | `Post.cs:122` |
| Retry max | 3 | `Post.cs:80` |
| SQS max receive count (→DLQ) | 3 | `template.yaml:106` |

**Upload is buffered, not streamed** — for local dev, the full request body is streamed via `Request.Body.CopyToAsync()`, but for S3 the frontend PUTs directly (no backend involvement). AI analysis loads entire files into memory (`ReadAllBytesAsync`).

---

## 4. Environment Separation

### Configuration Sources

| Setting | Dev | Prod | Source |
|---|---|---|---|
| DB connection | `Host=localhost;Port=5432;...` | Empty (from env var `DB_CONNECTION_STRING`) | `appsettings.Development.json` / env var |
| Meta App ID/Secret | Env vars `META_APP_ID`, `META_APP_SECRET` | Same (env vars) | Always env vars (no config file) |
| Meta Redirect URI | `http://localhost:5173/oauth/meta/callback` | Env var `META_REDIRECT_URI` | `appsettings.Development.json` / env var |
| Media storage | Local filesystem (`backend/uploads/`) | S3 bucket | `MEDIA_BUCKET_NAME` env var presence |
| Scheduler | Local polling (30s) | EventBridge | `SCHEDULER_TYPE` env var |
| Gemini API key | Env var `GEMINI_API_KEY` | Same | Always env var |
| Gemini model | Env var `GEMINI_MODEL` | Same | Always env var |
| AI rate limit | 200/day | 20/day | `appsettings.*.json` |
| CORS | `http://localhost:*` + any `https://` | Same permissive rule | `Startup.cs:126-129` |
| Swagger | Enabled | Disabled | `IsDevelopment()` check |
| HTTPS redirect | Enabled | Disabled (API GW handles it) | `IsRunningInLambda()` check |
| Public URL (for ngrok) | Env var `PUBLIC_URL` | N/A (S3 URLs used) | `LocalMediaService.cs:45` |

### Secrets Management

All secrets come from **environment variables** — no SSM/Secrets Manager integration exists in code yet:
- `META_APP_ID`, `META_APP_SECRET`
- `GEMINI_API_KEY`
- `DB_CONNECTION_STRING`

In the SAM template, these are passed as **CloudFormation parameters** with `NoEcho: true`.

---

## 5. AWS/Lambda Coupling Checklist

### NuGet Packages (`backend/PostPilot.Api.csproj`)

| Package | Version | Used For |
|---|---|---|
| `Amazon.Lambda.AspNetCoreServer` | 9.2.1 | `LambdaEntryPoint` base class |
| `Amazon.Lambda.CloudWatchEvents` | 4.1.0 | `CloudWatchEvent<T>` parameter type in Dispatcher/StuckRecovery |
| `Amazon.Lambda.SQSEvents` | 2.2.0 | `SQSEvent`, `SQSBatchResponse` in Publisher |
| `AWSSDK.Scheduler` | 3.7.* | `IAmazonScheduler` in EventBridgePostScheduler |
| `AWSSDK.S3` | 3.7.* | `IAmazonS3` in S3MediaService |
| `AWSSDK.SQS` | 3.7.* | `IAmazonSQS` in DispatcherFunction |

### AWS-Specific Code

| Coupling | Files | What It Does |
|---|---|---|
| **Lambda entry point** | `backend/LambdaEntryPoint.cs` | `APIGatewayHttpApiV2ProxyFunction` base class |
| **Lambda DI root** | `backend/Lambdas/LambdaStartup.cs` | Separate `ServiceCollection` for Lambda functions |
| **Dispatcher → SQS** | `backend/Lambdas/DispatcherFunction.cs` | `AmazonSQSClient`, `SendMessageAsync`, FIFO queue |
| **Publisher ← SQS** | `backend/Lambdas/PublisherFunction.cs` | `SQSEvent`, `SQSBatchResponse` |
| **StuckRecovery** | `backend/Lambdas/StuckRecoveryFunction.cs` | `CloudWatchEvent<object>`, `ILambdaContext` |
| **EventBridge Scheduler** | `backend/Services/Scheduling/EventBridgePostScheduler.cs` | `IAmazonScheduler`, `CreateScheduleRequest`, schedule ARNs |
| **S3 media storage** | `backend/Services/Media/S3MediaService.cs` | `IAmazonS3`, `GetPreSignedURL` |
| **Lambda detection** | `backend/Startup.cs:159-162` | `LAMBDA_TASK_ROOT` env var check |
| **Lambda video extractor** | `backend/Startup.cs:318-328` | Registers `NoOpVideoFrameExtractor` when in Lambda |
| **SAM template** | `backend/template.yaml` | Full infrastructure definition |
| **Post entity** | `backend/Entities/Post.cs:91` | `ScheduleArn` field (stores EventBridge ARN) |

### ENV Variables That Imply AWS

| Variable | Used In | Purpose |
|---|---|---|
| `LAMBDA_TASK_ROOT` | Startup.cs | Detect Lambda runtime |
| `SCHEDULER_TYPE` | Startup.cs | Switch between EventBridge/Local |
| `PUBLISHER_LAMBDA_ARN` | Startup.cs | EventBridge target |
| `SCHEDULER_ROLE_ARN` | Startup.cs | IAM role for scheduler |
| `EVENTBRIDGE_SCHEDULE_GROUP` | Startup.cs | Schedule group name |
| `MEDIA_BUCKET_NAME` | Startup.cs | Switch between S3/Local |
| `SQS_QUEUE_URL` | DispatcherFunction | SQS queue endpoint |

### Already Provider-Agnostic Code

The following are behind **clean interfaces** and would survive migration unchanged:
- `IPostScheduler` — already has `LocalPostScheduler` (polling) alongside `EventBridgePostScheduler`
- `IMediaService` — already has `LocalMediaService` alongside `S3MediaService`
- `IPostPublisher` / `IStoryPublisher` — platform publishers are HTTP-based, no AWS dependency
- `IVideoFrameExtractor` — already has FFmpeg and No-op implementations
- All controllers, services, middleware, entities, EF Core — standard ASP.NET Core

---

## 6. Questions / Unknowns

| # | Question | Where to Look |
|---|---|---|
| 1 | **Media cleanup**: No orphan cleanup exists. Is this intentional for MVP, or an oversight? | N/A — no code exists |
| 2 | **S3 validation gap**: `S3MediaService.GetLocalFilePath()` returns null. Does prod skip server-side validation entirely? | `backend/Services/Media/S3MediaService.cs:154` — yes, it's a known TODO |
| 3 | **SAM template says `Runtime: dotnet8`** but the project targets `net10.0`. Is the template stale, or is a custom runtime/container used? | `backend/template.yaml:43` vs `backend/PostPilot.Api.csproj:4` |
| 4 | **CORS policy** allows any `https://` origin — is this intentional for prod? | `backend/Startup.cs:126-129` |
| 5 | **No auth** — no JWT/API key/session middleware exists. Is this MVP-deliberate? | Full Startup.cs — no `UseAuthentication` / `UseAuthorization` |
| 6 | **LambdaStartup** registers Story publishers? It only registers `FacebookPagePublisher` and `InstagramPublisher` — no `InstagramStoryPublisher` / `FacebookStoryPublisher`. Stories may fail in Lambda. | `backend/Lambdas/LambdaStartup.cs:49-55` — missing story publishers |
| 7 | **Database connection pooling** — Lambda cold starts + no RDS Proxy could exhaust Postgres connections. What's the plan? | No code exists; CLAUDE.md mentions "RDS Proxy recommended soon" |
| 8 | **Video upload >200MB**: frontend XHR uploads entire file as one PUT. No multipart/resumable upload. What happens on timeout for large videos? | `frontend/src/api/media.ts:174` — single `xhr.send(file)` |

---

## Migration Summary (Lambda → Kestrel)

**Good news**: The architecture is already well-abstracted. The `IPostScheduler`, `IMediaService`, and publisher interfaces mean the core business logic has zero AWS dependency. The `LocalSchedulerBackgroundService` already proves the system works without Lambda/SQS/EventBridge.

**Migration surface**: You'd need to replace 6 files (3 Lambda functions + EventBridgePostScheduler + S3MediaService + LambdaEntryPoint) and could remove the 6 AWS NuGet packages. The `LocalSchedulerBackgroundService` already serves as the foundation for a Kestrel-based scheduler. Media storage would need a new `IMediaService` implementation for whatever storage you choose.

**Critical gaps to address regardless of migration**: Missing story publisher registration in `LambdaStartup`, no media cleanup/orphan handling, stale SAM runtime version (`dotnet8` vs `net10.0`), and `S3MediaService.GetLocalFilePath()` returning null (breaks server-side validation in prod).
