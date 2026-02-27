# Project Configuration & Environment Variables Analysis

This document is a full inventory of configuration sources and environment variables currently present in this repository, including runtime usage, defaults/fallbacks, precedence, and mismatches between templates and active code.

## 1) Configuration Surface Map

### Backend runtime (.NET)
- `backend/appsettings.json` (base defaults)
- `backend/appsettings.Development.json` (development overrides)
- `backend/appsettings.Production.json` (production overrides)
- Environment variables read directly via `Environment.GetEnvironmentVariable(...)`
- Environment variables mapped through .NET configuration providers (for example `ConnectionStrings__DefaultConnection`, `Logging__EnableEfSql`)
- `backend/Properties/launchSettings.json` (local `dotnet run` profile settings)

### Frontend runtime (Vite + React)
- `frontend/vite.config.ts` (dev server proxy configuration)
- No `import.meta.env.*` usage in `frontend/src` at this time

### Local infra / deployment tooling
- `docker-compose.yml` (local Postgres + pgAdmin credentials and ports)
- `backend/samconfig.toml` (SAM stack/deploy defaults)
- `.env` templates in `backend/` (documentation/onboarding; not auto-loaded by ASP.NET)

---

## 2) Environment Variables Used by Active Backend Code

Only variables read in non-generated backend source are included below.

| Variable | Required at startup? | Used in | Purpose | Behavior / fallback |
|---|---|---|---|---|
| `DB_CONNECTION_STRING` | Conditionally required | `Startup.ConfigureServices` | DB connection when `ConnectionStrings:DefaultConnection` is empty | Used only if appsettings connection is absent; startup throws if both missing |
| `META_APP_ID` | Yes | `Startup.ConfigureServices` | Meta OAuth app id | Startup throws if missing |
| `META_APP_SECRET` | Yes | `Startup.ConfigureServices` | Meta OAuth app secret | Startup throws if missing |
| `META_REDIRECT_URI` | Conditionally required | `Startup.ConfigureServices` | Meta OAuth redirect URI | Used if `Meta:RedirectUri` key is absent; startup throws if both missing |
| `APP_RUN_MODE` | No | `Startup.ConfigureMediaService` | Selects media mode (`server` vs local behavior) | Defaults to `local`; any value except `server` resolves to local |
| `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES` | No | `Startup.ConfigureMediaService` | Upload URL expiry in minutes | Parsed as `int`; invalid/missing value defaults to `60` |
| `GEMINI_API_KEY` | Soft-required (feature) | `Startup.ConfigureAiServices` | AI API key | Defaults to empty string; runtime AI requests can fail if unset |
| `GEMINI_MODEL` | Yes | `Startup.ConfigureAiServices` | Primary AI model id | Startup throws if missing |
| `GEMINI_VISION_MODEL` | No | `Startup.ConfigureAiServices` | Optional vision model override | Null when absent |
| `AI_LANGUAGE_DETECTOR_PROVIDER` | No | `Startup.ConfigureAiServices` | Language detector provider selector | Defaults to `gemini` |
| `AI_CAPTION_GENERATOR_PROVIDER` | No | `Startup.ConfigureAiServices` | Caption provider selector | Defaults to `gemini` |
| `PUBLIC_URL` | No | `LocalDiskMediaStorageProvider`, `MediaAiService` | Public backend base URL for generated media/frame URLs | Defaults to `http://localhost:5122` |

---

## 3) AppSettings / Configuration Keys Consumed by Code

| Key | Used in | Meaning |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | `Startup.ConfigureServices` | Primary DB connection string source |
| `Meta:RedirectUri` | `Startup.ConfigureServices` | Preferred Meta redirect URI source |
| `Features` | `Startup.ConfigureServices` (`GetSection("Features")`) | Binds `FeatureSettings` |
| `Features:PlatformSelection` | `Startup.ConfigureServices` (`Configure<PlatformSelectionOptions>`) | Binds platform selection options |
| `Ai:RateLimiter` | `Startup.ConfigureAiServices` (`Configure<AiRateLimiterOptions>`) | Binds AI rate limiter settings |
| `Logging:EnableEfSql` | `Program.cs` | Enables EF SQL log filters when true |

### Notes on keys present in appsettings but not actively consumed as config keys
- `Ai:LanguageDetectorProvider` and `Ai:CaptionGeneratorProvider` appear in appsettings files, but provider selection currently comes from env vars (`AI_LANGUAGE_DETECTOR_PROVIDER`, `AI_CAPTION_GENERATOR_PROVIDER`) in startup code.
- `AllowedHosts` exists in appsettings files; this app does not call `UseHostFiltering` explicitly, but ASP.NET host filtering middleware can consume this depending on hosting configuration.

---

## 4) Other Configuration Values (Non-App Runtime)

### `backend/Properties/launchSettings.json`
- Sets `ASPNETCORE_ENVIRONMENT=Development` for both `http` and `https` profiles.
- Sets local bind URLs:
  - HTTP profile: `http://localhost:5122`
  - HTTPS profile: `https://localhost:7288;http://localhost:5122`

### `docker-compose.yml`
- PostgreSQL:
  - `POSTGRES_USER=postgres`
  - `POSTGRES_PASSWORD=postgres`
  - `POSTGRES_DB=postpilot`
- pgAdmin:
  - `PGADMIN_DEFAULT_EMAIL=admin@postpilot.com`
  - `PGADMIN_DEFAULT_PASSWORD=admin`

These are local-dev defaults and should not be reused in shared/staging/prod environments.

### `backend/samconfig.toml`
- Defines SAM defaults (`stack_name`, IAM capabilities, change-set behavior).
- Defines environment-specific deploy parameter/tag sets for `dev`, `staging`, `prod`.

---

## 5) .env Template Coverage vs Actual Runtime Reads

### Templates reviewed
- `backend/.env.example.local`
- `backend/.env.example.server-dev`
- `backend/.env.example.server-prod`

### Covered and used in active code
- `APP_RUN_MODE`, `DB_CONNECTION_STRING`, `META_APP_ID`, `META_APP_SECRET`, `META_REDIRECT_URI`, `GEMINI_API_KEY`, `GEMINI_MODEL`, `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES`, `PUBLIC_URL`.

### Listed in templates but not read by active code
- `MEDIA_DOWNLOAD_IMAGE_EXPIRATION_MINUTES`
- `MEDIA_DOWNLOAD_VIDEO_EXPIRATION_MINUTES`

---

## 6) Precedence & Failure Modes

1. **Database connection**
   - Precedence: `ConnectionStrings:DefaultConnection` -> `DB_CONNECTION_STRING`.
   - Failure: app startup throws if neither is set.

2. **Meta OAuth settings**
   - `META_APP_ID` and `META_APP_SECRET` are required env vars.
   - Redirect precedence: `Meta:RedirectUri` -> `META_REDIRECT_URI`.
   - Failure: startup throws when required values are missing.

3. **AI settings**
   - `GEMINI_MODEL` is required (startup throws if missing).
   - `GEMINI_API_KEY` is not startup-validated; AI features can fail at call time if absent.
   - Provider env vars default to `gemini` if not set.

4. **Media URL generation**
   - `PUBLIC_URL` controls generated absolute media/frame URLs.
   - Defaults to `http://localhost:5122`, which is usually wrong for public production access unless explicitly overridden.

5. **Run mode behavior**
   - `APP_RUN_MODE=server` selects the server storage provider path.
   - Any other or missing value falls back to local-disk storage behavior.

---

## 7) Security / Operations Observations

- Secrets are intentionally sourced from environment variables in startup logic (good baseline).
- Local infra files and templates include plaintext example credentials that are appropriate for local setup only.
- `AllowedHosts` is `*` in current appsettings files.
- AI key is not fail-fast validated, so bad/missing key issues surface at runtime rather than startup.

---

## 8) Suggested Follow-ups

1. Add startup validation for non-empty `GEMINI_API_KEY` when AI endpoints are enabled.
2. Decide on a single source of truth for AI provider selectors (env vars vs appsettings keys) and remove the duplicate path.
3. Either implement handling for `MEDIA_DOWNLOAD_*_EXPIRATION_MINUTES` or remove those template entries.
4. Add a short “required env vars by environment” section in `README.md` for quicker onboarding.
5. For production, ensure `PUBLIC_URL` is explicitly set to the externally reachable backend origin.
