# Environment Variables Audit — Post Pilot

> Generated: 2026-02-28 | **Updated: 2026-02-28** (config hygiene PR applied)
> Scope: backend (.NET 10), frontend (React/Vite), infrastructure (Docker Compose)

---

## Table of Contents

1. [Env Var Catalog](#1-env-var-catalog)
   - [1A. True Environment Variables](#1a-true-environment-variables-read-via-environmentgetenvironmentvariable)
   - [1B. Configuration-Bound Settings](#1b-configuration-bound-settings-appsettings--config-json--env-var-override)
   - [1C. ASP.NET Core Framework Variables](#1c-aspnet-core-framework-variables)
   - [1D. Frontend Variables](#1d-frontend-variables)
   - [1E. Docker / Infrastructure Variables](#1e-docker--infrastructure-variables)
2. [Runtime Modes & Profiles](#2-runtime-modes--profiles)
3. [Security Review](#3-security-review)
4. [Cleanup List](#4-cleanup-list)
5. [Validation Plan](#5-validation-plan)
6. [Recommended Next Steps](#6-recommended-next-steps)

---

## 1. Env Var Catalog

### 1A. True Environment Variables (read via `Environment.GetEnvironmentVariable`)

These are read directly from environment variables in code, **not** through `IConfiguration` binding.

| Name | Layer | Required? | Default | Type | Example | Used in (file:line) | Purpose | Failure mode | Notes |
|------|-------|-----------|---------|------|---------|---------------------|---------|--------------|-------|
| `DB_CONNECTION_STRING` | backend | No (fallback) | none | string (connection string) | `Host=localhost;Port=5432;Database=postpilot;...` | [Startup.cs L42](backend/Startup.cs#L42) | PostgreSQL connection string, fallback when `ConnectionStrings:DefaultConnection` config is empty | Throws `InvalidOperationException` at startup if both config and env var are missing | Primary source is `appsettings.*.json`; env var is a fallback |
| `META_APP_ID` | backend | **Yes** | none | string | `123456789012345` | [Startup.cs L55-56](backend/Startup.cs#L55) | Facebook/Instagram Meta app ID for OAuth | Throws `InvalidOperationException` at startup | Secret — env-var only, no config fallback |
| `META_APP_SECRET` | backend | **Yes** | none | string | `abc123def456...` | [Startup.cs L58-59](backend/Startup.cs#L58) | Meta app secret for OAuth token exchange | Throws `InvalidOperationException` at startup | **SECRET** — env-var only |
| `META_REDIRECT_URI` | backend | No (fallback) | none | string (URL) | `https://yourapp.com/oauth/meta/callback` | [Startup.cs L63](backend/Startup.cs#L63) | OAuth redirect callback URI | Throws `InvalidOperationException` if both `Meta:RedirectUri` config and env var are missing | Primary source is `appsettings.*.json` (`Meta:RedirectUri`) |
| `APP_RUN_MODE` | backend | No | `"local"` | string (`local` \| `server`) | `local` | [Startup.cs L179](backend/Startup.cs#L179) | Selects media storage strategy: local filesystem vs. remote storage provider | Defaults to `local` if missing or unrecognized | Not validated beyond case-insensitive `"server"` check |
| `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES` | backend | No | `-1` (uses config) | int (minutes) | `60` | [Startup.cs L207](backend/Startup.cs#L207) | Overrides `Media:UploadUrlExpirationMinutes` config for upload URL TTL | Falls back to config value if missing/unparseable | Parsed with `int.TryParse`; invalid → uses config default |
| `PUBLIC_URL` | backend | No | `Media:LocalServerBaseUrl` config | string (URL) | `https://abc123.ngrok-free.app` | [LocalDiskMediaStorageProvider.cs L17](backend/Services/Media/LocalDiskMediaStorageProvider.cs#L17), [MediaAiService.cs L226](backend/Services/Ai/MediaAiService.cs#L226), [MediaAiService.cs L303](backend/Services/Ai/MediaAiService.cs#L303) | Overrides the base URL used to generate download URLs (for tunneling/ngrok in local mode) | Falls back to `LocalServerBaseUrl` from `MediaOptions` | Read in 3 separate places — no single source of truth |
| `GEMINI_API_KEY` | backend | Yes (soft) | `""` (empty string) | string | `AIzaSy...` | [Startup.cs L273](backend/Startup.cs#L273) | Google Gemini API key for AI features | Defaults to empty string — AI calls will fail at runtime, not at startup | **SECRET** — env-var only; GeminiSettingsValidator does NOT validate this |
| `GEMINI_MODEL` | backend | **Yes** | none | string | `gemini-2.0-flash` | [Startup.cs L274-275](backend/Startup.cs#L274) | Gemini model name for text generation | Throws `InvalidOperationException` at startup | Env-var only, no config fallback |
| `GEMINI_VISION_MODEL` | backend | No | `null` (falls back to `GEMINI_MODEL`) | string | `gemini-2.0-flash` | [Startup.cs L276](backend/Startup.cs#L276) | Separate model for vision/image tasks | Falls back to `Model` at usage time | Optional override |
| `AI_LANGUAGE_DETECTOR_PROVIDER` | backend | No | config value | string (`gemini`) | `gemini` | [Startup.cs L287-288](backend/Startup.cs#L287) | Overrides `Ai:Providers:LanguageDetectorProvider` config | Uses config value if env var is empty/missing | PostConfigure override |
| `AI_CAPTION_GENERATOR_PROVIDER` | backend | No | config value | string (`gemini`) | `gemini` | [Startup.cs L289-290](backend/Startup.cs#L289) | Overrides `Ai:Providers:CaptionGeneratorProvider` config | Uses config value if env var is empty/missing | PostConfigure override |

### 1B. Configuration-Bound Settings (appsettings + config JSON + env var override)

These come from the layered configuration system: `appsettings.json` → `appsettings.{env}.json` → `config/appsettings.common.json` → `config/appsettings.{appEnv}.json` → environment variables (via `.AddEnvironmentVariables()`). Environment variables can override any of these using `__` as section separator (e.g., `Media__LocalServerBaseUrl`).

#### Meta API Options (`Meta:Api` section → `MetaApiOptions`)

| Config Key | Required? | Default (from common.json) | Type | Validated? | Purpose |
|------------|-----------|---------------------------|------|------------|---------|
| `Meta:Api:GraphApiBaseUrl` | Yes | `https://graph.facebook.com/v21.0` | string (URL) | Yes — `MetaApiOptionsValidator`: must be non-empty absolute URI | Base URL for all Graph API calls |
| `Meta:Api:OAuthDialogBaseUrl` | Yes | `https://www.facebook.com/v21.0/dialog/oauth` | string (URL) | Yes — must be non-empty absolute URI | Base URL for OAuth dialog redirect |

Bound in [Startup.cs L118-122](backend/Startup.cs#L118). Consumed by: `MetaOAuthService`, `FacebookPagePublisher`, `InstagramPublisher`, `InstagramStoryPublisher`, `FacebookStoryPublisher`, `FacebookInsightsService`.

#### Meta OAuth Settings (mixed sources → `MetaOAuthSettings`)

| Config Key / Env Var | Required? | Default | Type | Validated? | Purpose |
|---------------------|-----------|---------|------|------------|---------|
| `Meta:RedirectUri` / `META_REDIRECT_URI` | Yes (one must exist) | `http://localhost:5173/oauth/meta/callback` (in `appsettings.Development.json`) | string (URL) | No formal validator — throws at startup if all sources missing | OAuth callback URL |

Bound manually in [Startup.cs L62-70](backend/Startup.cs#L62). Consumed by `MetaOAuthService`.

#### Publishing Options (`Publishing` section → `PublishingOptions`)

| Config Key | Required? | Default (from common.json) | Type | Validated? | Purpose |
|------------|-----------|---------------------------|------|------------|---------|
| `Publishing:WorkerPollIntervalSeconds` | Yes | `30` | int | Yes (> 0) | Background worker polling frequency |
| `Publishing:StuckPostThresholdMinutes` | Yes | `5` | int | Yes (> 0) | Threshold for stuck post recovery |
| `Publishing:StuckPostRetryDelaySeconds` | Yes | `10` | int | Yes (> 0) | Delay before retrying stuck posts |
| `Publishing:MediaDownloadUrlExpirationMinutes` | Yes | `60` | int | Yes (> 0) | Image download URL TTL for Meta |
| `Publishing:VideoDownloadUrlExpirationMinutes` | Yes | `120` | int | Yes (> 0) | Video download URL TTL for Meta |
| `Publishing:ImagePollMaxAttempts` | Yes | `30` | int | Yes (> 0) | Instagram container status poll retries |
| `Publishing:ImagePollIntervalSeconds` | Yes | `2` | int | Yes (> 0) | Instagram container status poll interval |
| `Publishing:OAuthStateExpirationMinutes` | Yes | `10` | int | Yes (> 0) | OAuth state parameter TTL |

Bound in [Startup.cs L125-130](backend/Startup.cs#L125). Validated by `PublishingOptionsValidator`.

#### Media Options (`Media` section → `MediaOptions`)

| Config Key | Required? | Default (from common.json) | Type | Validated? | Purpose |
|------------|-----------|---------------------------|------|------------|---------|
| `Media:UploadUrlExpirationMinutes` | Yes | `60` | int | Yes (> 0) | Upload URL TTL (can be overridden by `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES` env var) |
| `Media:MaxImageFileSizeBytes` | Yes | `20971520` (20 MB) | long | Yes (> 0) | Max image upload size |
| `Media:MaxVideoFileSizeBytes` | Yes | `209715200` (200 MB) | long | Yes (> 0) | Max video upload size |
| `Media:LocalServerBaseUrl` | Yes | `http://localhost:5122` | string (URL) | Yes (non-empty, absolute URI) | Base URL for local media serving |

Bound in [Startup.cs L132-136](backend/Startup.cs#L132). Validated by `MediaOptionsValidator`.

#### Feature Settings (`Features` section → `FeatureSettings`)

| Config Key | Required? | Default (from common.json) | Type | Validated? | Purpose |
|------------|-----------|---------------------------|------|------------|---------|
| `Features:EnableEngagementFetch` | No | `false` | bool | No | Toggle Facebook engagement metrics fetch |
| `Features:EnableFacebookThumbnail` | No | `false` | bool | No | Toggle custom video thumbnails for Facebook |
| `Features:PlatformSelection:MaxPlatformsPerPost` | Yes | `1` | int | Yes (> 0) | Max simultaneous platform targets per post |

Bound in [Startup.cs L108-116](backend/Startup.cs#L108). `PlatformSelectionOptions` validated by `PlatformSelectionOptionsValidator`.

#### Gemini Settings (`Ai:Gemini` section → `GeminiSettings`)

| Config Key / Env Var | Required? | Default (from common.json) | Type | Validated? | Purpose |
|---------------------|-----------|---------------------------|------|------------|---------|
| `Ai:Gemini:BaseUrl` | Yes | `https://generativelanguage.googleapis.com/v1beta` | string (URL) | Yes (non-empty, absolute URI) | Gemini API base endpoint |
| `Ai:Gemini:TimeoutSeconds` | Yes | `30` | int | Yes (> 0) | HTTP timeout for Gemini calls |
| `GEMINI_API_KEY` (env only) | Yes (soft) | `""` | string | **No** — validator skips this field | API key for Gemini |
| `GEMINI_MODEL` (env only) | **Yes** | none | string | **No** — validator skips this field | Model name |
| `GEMINI_VISION_MODEL` (env only) | No | `null` | string | No | Vision model name |

Bound via `.Bind()` + `.PostConfigure()` in [Startup.cs L269-280](backend/Startup.cs#L269). The `PostConfigure` step injects env vars into already-bound settings. Validated by `GeminiSettingsValidator` — **but the validator only checks `BaseUrl` and `TimeoutSeconds`**, not `ApiKey` or `Model`.

#### AI Provider Settings (`Ai:Providers` section → `AiProviderSettings`)

| Config Key / Env Var | Required? | Default (from common.json) | Type | Validated? | Purpose |
|---------------------|-----------|---------------------------|------|------------|---------|
| `Ai:Providers:LanguageDetectorProvider` / `AI_LANGUAGE_DETECTOR_PROVIDER` | Yes | `gemini` | string | Yes (non-empty) | Which AI backend for language detection |
| `Ai:Providers:CaptionGeneratorProvider` / `AI_CAPTION_GENERATOR_PROVIDER` | Yes | `gemini` | string | Yes (non-empty) | Which AI backend for caption generation |

Bound in [Startup.cs L283-294](backend/Startup.cs#L283). Validated by `AiProviderSettingsValidator`.

#### AI Rate Limiter Options (`Ai:RateLimiter` → `AiRateLimiterOptions`)

| Config Key | Required? | Default (from common.json) | Type | Validated? | Purpose |
|------------|-----------|---------------------------|------|------------|---------|
| `Ai:RateLimiter:MaxCallsPerDay` | Yes | `20` (common) / `200` (Development) | int | Yes (> 0) | Max AI requests per user per window |
| `Ai:RateLimiter:WindowHours` | Yes | `24` | int | Yes (> 0) | Rate-limit window size |

Bound in [Startup.cs L256-258](backend/Startup.cs#L256). Validated by `AiRateLimiterOptionsValidator`.

#### AI Cache Options (`Ai:CacheDurations` → `AiCacheOptions`)

| Config Key | Required? | Default (from common.json) | Type | Validated? | Purpose |
|------------|-----------|---------------------------|------|------------|---------|
| `Ai:CacheDurations:CaptionAssistMinutes` | Yes | `60` | int | Yes (> 0) | Cache TTL for caption assist |
| `Ai:CacheDurations:LanguageDetectionMinutes` | Yes | `1440` | int | Yes (> 0) | Cache TTL for language detection |
| `Ai:CacheDurations:GoogleAiClientMinutes` | Yes | `60` | int | Yes (> 0) | Cache TTL for Gemini text calls |
| `Ai:CacheDurations:PostTimeSuggestionMinutes` | Yes | `10` | int | Yes (> 0) | Cache TTL for time suggestions |
| `Ai:CacheDurations:AssetResolverDownloadUrlExpirationMinutes` | Yes | `15` | int | Yes (> 0) | Download URL TTL for AI asset resolver |

Bound in [Startup.cs L262-266](backend/Startup.cs#L262). Validated by `AiCacheOptionsValidator`.

#### Miscellaneous Config Keys

| Config Key | Required? | Default | Type | File | Purpose |
|------------|-----------|---------|------|------|---------|
| `ConnectionStrings:DefaultConnection` | Yes (or `DB_CONNECTION_STRING`) | `""` (appsettings.json) / full string (Development) | string | [Startup.cs L41](backend/Startup.cs#L41) | Primary DB connection source |
| `Logging:EnableEfSql` | No | `false` | bool | [Program.cs L36](backend/Program.cs#L36) | Toggle verbose EF Core SQL logging |
| `Ffprobe:Path` | No | `"ffprobe"` | string (path) | [FfprobeVideoMetadataExtractor.cs L22](backend/Services/Validation/FfprobeVideoMetadataExtractor.cs#L22) | Custom path to ffprobe binary |

### 1C. ASP.NET Core Framework Variables

| Name | Layer | Required? | Default | Used in | Purpose |
|------|-------|-----------|---------|---------|---------|
| `ASPNETCORE_ENVIRONMENT` | backend/infra | No | `Production` | [launchSettings.json L10](backend/Properties/launchSettings.json#L10), .env.example files | Controls ASP.NET hosting environment (`Development` / `Staging` / `Production`) which drives config file layering and Swagger enablement |
| `ASPNETCORE_URLS` | backend/infra | No | per launchSettings | [launchSettings.json L8](backend/Properties/launchSettings.json#L8) | Kestrel listen URLs (set via `applicationUrl` in launchSettings) |

### 1D. Frontend Variables

| Name | Layer | Required? | Default | Type | Example | Used in (file:line) | Purpose | Failure mode | Notes |
|------|-------|-----------|---------|------|---------|---------------------|---------|--------------|-------|
| `VITE_API_URL` | frontend | No | (see config files) | string (URL) | `http://localhost:5122/api` | [appConfig.ts L32-33](frontend/src/config/appConfig.ts#L32) | Overrides `apiBaseUrl` from config JSON files | Falls back to mode-specific config → common.json | Documented in [.env.example](frontend/.env.example) |
| `MODE` (Vite built-in) | frontend | No (auto) | `development` | string | `development` / `staging` / `production` | [appConfig.ts L11](frontend/src/config/appConfig.ts#L11) | Determines which config JSON to load | Defaults to `{}` if mode doesn't match known values | Set by Vite based on `--mode` flag |

**Frontend config file chain:**
- `config/common.json` → `apiBaseUrl: "http://localhost:5122/api"`
- `config/local.json` → `apiBaseUrl: "http://localhost:5122/api"` (same as common)
- `config/dev.json` → `{}` (empty — inherits common)
- `config/prod.json` → `{}` (empty — inherits common)
- `VITE_API_URL` env var → final override

### 1E. Docker / Infrastructure Variables

From [docker-compose.yml](docker-compose.yml):

| Name | Service | Required? | Default | Purpose |
|------|---------|-----------|---------|---------|
| `POSTGRES_USER` | postgres | Yes | `postgres` (hardcoded) | PostgreSQL superuser name |
| `POSTGRES_PASSWORD` | postgres | Yes | `postgres` (hardcoded) | PostgreSQL superuser password |
| `POSTGRES_DB` | postgres | Yes | `postpilot` (hardcoded) | Default database name |
| `PGADMIN_DEFAULT_EMAIL` | pgadmin | Yes | `admin@postpilot.com` (hardcoded) | pgAdmin login email |
| `PGADMIN_DEFAULT_PASSWORD` | pgadmin | Yes | `admin` (hardcoded) | pgAdmin login password |

---

## 2. Runtime Modes & Profiles

### A. ASP.NET Environment → Config Layering

The mapping is defined in [Program.cs L13-18](backend/Program.cs#L13):

| `ASPNETCORE_ENVIRONMENT` | `appEnv` slug | Config files loaded (in order) |
|--------------------------|---------------|-------------------------------|
| `Development` | `local` | `appsettings.json` → `appsettings.Development.json` → `config/appsettings.common.json` → `config/appsettings.local.json`* → env vars |
| `Staging` | `dev` | `appsettings.json` → `appsettings.Staging.json`* → `config/appsettings.common.json` → `config/appsettings.dev.json` → env vars |
| `Production` | `prod` | `appsettings.json` → `appsettings.Production.json` → `config/appsettings.common.json` → `config/appsettings.prod.json` → env vars |

*Note: `config/appsettings.local.json` does not exist (only `config/appsettings.common.json`, `.dev.json`, `.prod.json` were found). `appsettings.Staging.json` also does not exist. Both are loaded with `optional: true` so this is not a problem, but it means those layers are no-ops.*

### B. `APP_RUN_MODE` (Media Storage Strategy)

| Value | Behavior | Where |
|-------|----------|-------|
| `local` (default) | Uses `LocalDiskMediaStorageProvider` — files stored on local disk under `uploads/` | [Startup.cs L179-198](backend/Startup.cs#L179) |
| `server` | Uses `ServerMediaStorageProvider` (stub — throws `NotImplementedException`) | [Startup.cs L186-189](backend/Startup.cs#L186) |

### C. `.env.example` Profiles

Three `.env.example` files define expected env vars per deployment mode:

| File | Profile | Key vars |
|------|---------|----------|
| `.env.example.local` | Local dev | `APP_RUN_MODE=local`, no `ASPNETCORE_ENVIRONMENT`, no `META_REDIRECT_URI` |
| `.env.example.server-dev` | Server dev | `APP_RUN_MODE=server`, `ASPNETCORE_ENVIRONMENT=Development`, `META_REDIRECT_URI` set |
| `.env.example.server-prod` | Server prod | `APP_RUN_MODE=server`, `ASPNETCORE_ENVIRONMENT=Production`, `META_REDIRECT_URI` set |

### D. Frontend Modes

| Vite `--mode` | Maps to | Config loaded |
|---------------|---------|---------------|
| `development` (default for `dev`) | `local` | `config/local.json` |
| `staging` | `dev` | `config/dev.json` |
| `production` (default for `build`) | `prod` | `config/prod.json` |

---

## 3. Security Review

### Secrets Identified

| Env Var / Config Key | Classification | Stored Where | Logged? | Client-Side? | Status |
|---------------------|----------------|--------------|---------|--------------|--------|
| `META_APP_SECRET` | **Secret** | Env var only | No (log says "without secrets") | No | **OK** — appropriately handled. Log at [MetaOAuthService.cs L170](backend/Services/MetaOAuthService.cs#L170) explicitly redacts. |
| `META_APP_ID` | Semi-secret (public in Meta dashboard, but not for logs) | Env var only | Not directly | No | OK |
| `GEMINI_API_KEY` | **Secret** | Env var only | Not directly logged | No | **FIXED**: Key was previously in URL query string (`?key=...`). Now sent via `x-goog-api-key` HTTP header in `GoogleAiClientBase` and `PostTimeSuggestionService`. |
| `DB_CONNECTION_STRING` / `ConnectionStrings:DefaultConnection` | **Secret** (contains password) | Config file (Development) / env var (prod) | Not intentionally | No | **CONCERN**: `appsettings.Development.json` contains hardcoded `Password=postgres` which is committed to git. Acceptable for local dev, but pattern could be copied to production. |
| `POSTGRES_PASSWORD` | **Secret** | docker-compose.yml (hardcoded) | N/A | No | **OK for local dev only** — should not be used in production |
| `PGADMIN_DEFAULT_PASSWORD` | **Secret** | docker-compose.yml (hardcoded) | N/A | No | OK for local dev |

### Client-Side Safety

- **No secrets are exposed client-side.** The only frontend env var is `VITE_API_URL` (a non-secret URL).
- `META_APP_ID` is used server-side only — it is NOT exposed to the frontend bundle.

### Recommendations

1. ~~**GEMINI_API_KEY in URLs**: Consider passing the API key via HTTP header (`x-goog-api-key`) instead of query string to prevent accidental logging.~~ **DONE** — moved to `x-goog-api-key` header.
2. **DB password in appsettings.Development.json**: Add a comment clarifying this is local-only. Consider using User Secrets for local dev.
3. ~~**`GEMINI_API_KEY` not validated at startup**~~ **DONE** — `GeminiSettingsValidator` now checks `ApiKey` and `Model` are non-empty.

---

## 4. Cleanup List

### A. Unused / Ghost Variables (in .env.example but NOT in code)

| Variable | Where mentioned | Status |
|----------|----------------|--------|
| `MEDIA_LOCAL_ROOT` | [.env.example.local L21](backend/.env.example.local#L21) | **GHOST** — never read in code. `LocalDiskMediaStorageProvider` always uses `Path.Combine(Directory.GetCurrentDirectory(), "uploads")` hardcoded. Remove from .env.example or implement support. |
| `MEDIA_DOWNLOAD_IMAGE_EXPIRATION_MINUTES` | [.env.example.server-dev L24](backend/.env.example.server-dev#L24), [.env.example.server-prod L24](backend/.env.example.server-prod#L24) | **GHOST** — never read in code. `Publishing:MediaDownloadUrlExpirationMinutes` is the actual config key (read from JSON config, not env var). Remove from .env.example or rename to match. |
| `MEDIA_DOWNLOAD_VIDEO_EXPIRATION_MINUTES` | [.env.example.server-dev L25](backend/.env.example.server-dev#L25), [.env.example.server-prod L25](backend/.env.example.server-prod#L25) | **GHOST** — never read in code. `Publishing:VideoDownloadUrlExpirationMinutes` is the actual config key. Same issue as above. |

### B. Redundant / Dual-Path Variables

| Variable(s) | Issue | Recommendation |
|-------------|-------|----------------|
| `DB_CONNECTION_STRING` vs `ConnectionStrings:DefaultConnection` | Two names for the same value. Code checks config first, then env var. `.AddEnvironmentVariables()` already allows `ConnectionStrings__DefaultConnection` as env var. | Remove `DB_CONNECTION_STRING` support; use `ConnectionStrings__DefaultConnection` env var (standard .NET convention). |
| `META_REDIRECT_URI` vs `Meta:RedirectUri` | Two names for the same value. Code checks config first, then env var. `.AddEnvironmentVariables()` already allows `Meta__RedirectUri` as env var. | Remove `META_REDIRECT_URI`; use `Meta__RedirectUri` env var or keep in appsettings only. |
| `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES` vs `Media:UploadUrlExpirationMinutes` | Env var overrides config, but `.AddEnvironmentVariables()` already supports `Media__UploadUrlExpirationMinutes`. The manual `int.TryParse` in [Startup.cs L207](backend/Startup.cs#L207) is redundant. | Remove manual env var parsing; rely on config binding + `Media__UploadUrlExpirationMinutes` env var. |
| `AI_LANGUAGE_DETECTOR_PROVIDER` / `AI_CAPTION_GENERATOR_PROVIDER` vs `Ai:Providers:*` | Env vars override config via `PostConfigure`. `.AddEnvironmentVariables()` already supports `Ai__Providers__LanguageDetectorProvider`. | Remove `PostConfigure` overrides; use standard `__`-separated env var names. |

### C. `PUBLIC_URL` — Scattered Reads

`PUBLIC_URL` is read in **3 separate places** via `Environment.GetEnvironmentVariable`:

1. [LocalDiskMediaStorageProvider.cs L17](backend/Services/Media/LocalDiskMediaStorageProvider.cs#L17)
2. [MediaAiService.cs L226](backend/Services/Ai/MediaAiService.cs#L226)
3. [MediaAiService.cs L303](backend/Services/Ai/MediaAiService.cs#L303)

**Problem**: No single source of truth. If renamed, must update 3 locations. Should be centralized into `MediaOptions` or a dedicated config class and injected via DI.

### D. Naming Inconsistencies

| Current Name | Issue | Suggested Rename |
|-------------|-------|------------------|
| `APP_RUN_MODE` | Generic name; not obvious what "app run mode" controls | `MEDIA_STORAGE_MODE` or fold into config as `Media:StorageMode` |
| `GEMINI_MODEL` / `GEMINI_VISION_MODEL` | Not self-describing that these are AI model IDs | `AI_TEXT_MODEL` / `AI_VISION_MODEL` (or keep, but document better) |
| `META_APP_ID` / `META_APP_SECRET` | Prefixed with `META_` but `.env.example` files don't group them consistently | OK — but consider `META_OAUTH_APP_ID` / `META_OAUTH_APP_SECRET` for clarity |

### E. Missing `.env.example` Entries

| Variable | Missing from | Notes |
|----------|-------------|-------|
| `GEMINI_VISION_MODEL` | All `.env.example` files | Optional, but should be documented |
| `AI_LANGUAGE_DETECTOR_PROVIDER` | All `.env.example` files | Optional override, but should be documented |
| `AI_CAPTION_GENERATOR_PROVIDER` | All `.env.example` files | Optional override, but should be documented |
| `PUBLIC_URL` | `.env.example.server-dev`, `.env.example.server-prod` | Currently only in `.env.example.local` (commented) |

---

## 5. Validation Plan

### Backend: Startup Validation

#### Current State

| Mechanism | Variables/Settings | Status |
|-----------|-------------------|--------|
| `IValidateOptions<T>` + `.ValidateOnStart()` | MetaApiOptions, PublishingOptions, MediaOptions, PlatformSelectionOptions, AiRateLimiterOptions, AiCacheOptions, GeminiSettings (partial), AiProviderSettings | **Good** — fails fast at startup with descriptive messages |
| Manual `throw new InvalidOperationException` | `DB_CONNECTION_STRING`, `META_APP_ID`, `META_APP_SECRET`, `META_REDIRECT_URI`, `GEMINI_MODEL` | **OK** — fails fast but error messages lack structure |
| No validation | `GEMINI_API_KEY`, `APP_RUN_MODE`, `PUBLIC_URL`, `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES` | **Gap** — silent failures at runtime |

#### Proposed Improvements

1. **Extend `GeminiSettingsValidator`** to validate `ApiKey` (non-empty) and `Model` (non-empty). Move the `PostConfigure` throw into the validator for consistency:

    ```csharp
    // In GeminiSettingsValidator.Validate():
    if (string.IsNullOrWhiteSpace(options.ApiKey))
        failures.Add("GEMINI_API_KEY environment variable is required.");
    if (string.IsNullOrWhiteSpace(options.Model))
        failures.Add("GEMINI_MODEL environment variable is required.");
    ```

2. **Validate `APP_RUN_MODE`** — add an enum parse check:
    ```csharp
    if (runModeStr is not ("local" or "server"))
        throw new InvalidOperationException(
            $"APP_RUN_MODE must be 'local' or 'server', got '{runModeStr}'.");
    ```

3. **Centralize `PUBLIC_URL`** into `MediaOptions`:
    - Add `public string? PublicUrl { get; set; }` to `MediaOptions`
    - Set it in `PostConfigure` from `PUBLIC_URL` env var
    - Inject via DI instead of reading `Environment.GetEnvironmentVariable` in 3 places

4. **Consolidate all manual env var reads** into the Options pattern:
    - Move `META_APP_ID`, `META_APP_SECRET` into a `MetaOAuthOptions` class bound from config + `PostConfigure` env overrides
    - Create a `MetaOAuthOptionsValidator` for these
    - This eliminates manual `throw` statements in `ConfigureServices`

5. **Add a startup health-check log** that prints which env vars are set (without values) for troubleshooting:
    ```
    [INFO] Configuration summary:
      DB_CONNECTION_STRING: ✓ (from config)
      META_APP_ID: ✓
      META_APP_SECRET: ✓
      GEMINI_API_KEY: ✓
      GEMINI_MODEL: gemini-2.0-flash
      APP_RUN_MODE: local
      PUBLIC_URL: (not set, using LocalServerBaseUrl)
    ```

### Frontend: Build-Time Validation

#### Current State

No validation. If `VITE_API_URL` is missing and config files have empty `apiBaseUrl`, the app will silently send requests to `undefined/api/...`.

#### Proposed Improvements

1. **Add a config validation function** that runs at import time:

    ```typescript
    // In appConfig.ts
    function validateConfig(config: AppConfig): AppConfig {
      if (!config.apiBaseUrl) {
        throw new Error(
          'Missing apiBaseUrl. Set VITE_API_URL env var or configure config/*.json files.'
        )
      }
      try {
        new URL(config.apiBaseUrl)
      } catch {
        throw new Error(`Invalid apiBaseUrl: "${config.apiBaseUrl}" is not a valid URL.`)
      }
      return config
    }

    export const config: AppConfig = validateConfig(buildConfig())
    ```

2. **Add a Vite plugin or build script** that checks required env vars before build:

    ```typescript
    // vite.config.ts
    const requiredForProd = ['VITE_API_URL'] // only for production builds
    if (process.env.NODE_ENV === 'production') {
      for (const key of requiredForProd) {
        if (!process.env[key]) {
          throw new Error(`Missing required env var: ${key}`)
        }
      }
    }
    ```

---

## 6. Recommended Next Steps

### Completed (config hygiene PR)

| # | Action | Status |
|---|--------|--------|
| 1 | Remove ghost vars (`MEDIA_LOCAL_ROOT`, `MEDIA_DOWNLOAD_IMAGE_EXPIRATION_MINUTES`, `MEDIA_DOWNLOAD_VIDEO_EXPIRATION_MINUTES`) from `.env.example` files | **Done** |
| 2 | Add `GEMINI_VISION_MODEL`, `AI_LANGUAGE_DETECTOR_PROVIDER`, `AI_CAPTION_GENERATOR_PROVIDER`, `PUBLIC_URL` to `.env.example` files (as commented optional vars) | **Done** |
| 3 | Add `GEMINI_API_KEY` + `GEMINI_MODEL` validation to `GeminiSettingsValidator` — startup fails fast with clear error | **Done** |
| 5 | Centralize `PUBLIC_URL` into `MediaOptions.PublicUrl` + `EffectiveBaseUrl` — removed 3 scattered `Environment.GetEnvironmentVariable("PUBLIC_URL")` calls | **Done** |
| 8 | Add TODO deprecation comments for `DB_CONNECTION_STRING` and `META_REDIRECT_URI` — prefer `ConnectionStrings__DefaultConnection` / `Meta__RedirectUri` | **Done** |
| 14 | Move `GEMINI_API_KEY` from URL query string to `x-goog-api-key` HTTP header (security fix) | **Done** |

### Priority 1 — Remaining Quick Wins

| # | Action | Effort |
|---|--------|--------|
| 4 | Add `APP_RUN_MODE` value validation (must be `local` or `server`) | 5 min |

### Priority 2 — Centralization (minor refactors)

| # | Action | Effort |
|---|--------|--------|
| 6 | Move `MetaOAuthSettings` (`META_APP_ID`, `META_APP_SECRET`, `META_REDIRECT_URI`) into a proper Options class with validator | 45 min |
| 7 | Remove `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES` manual parsing — use `Media__UploadUrlExpirationMinutes` standard env var override | 15 min |

### Priority 3 — Cleanup & Consolidation

| # | Action | Effort |
|---|--------|--------|
| 9 | Remove `DB_CONNECTION_STRING` support once all deployments migrated to `ConnectionStrings__DefaultConnection` | 10 min |
| 10 | Remove `META_REDIRECT_URI` support once all deployments migrated to `Meta__RedirectUri` | 10 min |
| 11 | Remove `AI_LANGUAGE_DETECTOR_PROVIDER` / `AI_CAPTION_GENERATOR_PROVIDER` PostConfigure overrides — document `Ai__Providers__LanguageDetectorProvider` etc. | 10 min |
| 12 | Add frontend config validation in `appConfig.ts` | 15 min |
| 13 | Add startup configuration summary log (non-secret values only) | 20 min |

### Priority 4 — Documentation

| # | Action | Effort |
|---|--------|--------|
| 14 | Consolidate `.env.example.local`, `.env.example.server-dev`, `.env.example.server-prod` into a single `.env.example` with clear section comments | 20 min |
