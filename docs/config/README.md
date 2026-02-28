# Post Pilot Configuration

## Backend Configuration

### Config Precedence (last wins)

1. `backend/appsettings.json` (base defaults)
2. `backend/appsettings.{ASPNETCORE_ENVIRONMENT}.json` (ASP.NET Core built-in override)
3. `backend/config/appsettings.common.json` (shared non-secret config — optional)
4. `backend/config/appsettings.{appEnv}.json` (environment-specific — optional)
5. Environment variables (highest priority)

The `appEnv` value is derived from `ASPNETCORE_ENVIRONMENT`:

| ASPNETCORE_ENVIRONMENT | appEnv  | Config file loaded                      |
|------------------------|---------|-----------------------------------------|
| Development            | local   | `config/appsettings.local.json`         |
| Staging                | dev     | `config/appsettings.dev.json`           |
| Production             | prod    | `config/appsettings.prod.json`          |

All `config/` files are optional — the app runs fine without them.

### Backend Environment Variables

| Variable                            | Required | Description                                        |
|-------------------------------------|----------|----------------------------------------------------|
| `ASPNETCORE_ENVIRONMENT`            | No       | ASP.NET environment (Development/Staging/Production). Defaults to Production. |
| `DB_CONNECTION_STRING`              | No*      | PostgreSQL connection string. Fallback: `ConnectionStrings:DefaultConnection` in appsettings. |
| `META_APP_ID`                       | Yes      | Meta (Facebook/Instagram) OAuth App ID             |
| `META_APP_SECRET`                   | Yes      | Meta OAuth App Secret                              |
| `META_REDIRECT_URI`                 | No*      | Meta OAuth redirect URI. Fallback: `Meta:RedirectUri` in appsettings. |
| `APP_RUN_MODE`                      | No       | `local` (default) or `server`                      |
| `MEDIA_UPLOAD_URL_EXPIRATION_MINUTES` | No     | Upload URL expiration in minutes. Overrides `Media:UploadUrlExpirationMinutes`. Default: 60 |
| `GEMINI_API_KEY`                    | No       | Google AI API key (empty = AI features degrade gracefully) |
| `GEMINI_MODEL`                      | Yes      | Gemini model name (e.g., `gemini-2.0-flash`)       |
| `GEMINI_VISION_MODEL`              | No       | Optional separate vision model                      |
| `AI_LANGUAGE_DETECTOR_PROVIDER`     | No       | AI provider for language detection. Default: `gemini` |
| `AI_CAPTION_GENERATOR_PROVIDER`     | No       | AI provider for caption generation. Default: `gemini` |

*At least one of the env var or appsettings value must be set.

### Backend Config Sections (appsettings / config/*.json)

#### `Meta:Api` — Meta Graph API settings

| Key                  | Default                                              | Description                     |
|----------------------|------------------------------------------------------|---------------------------------|
| `GraphApiBaseUrl`    | `https://graph.facebook.com/v21.0`                   | Meta Graph API base URL         |
| `OAuthDialogBaseUrl` | `https://www.facebook.com/v21.0/dialog/oauth`        | Facebook OAuth dialog URL       |

#### `Publishing` — Publishing pipeline settings

| Key                               | Default | Description                                              |
|-----------------------------------|---------|----------------------------------------------------------|
| `WorkerPollIntervalSeconds`       | `30`    | Background worker polling interval                       |
| `StuckPostThresholdMinutes`       | `5`     | Minutes before a stuck post is recovered                 |
| `StuckPostRetryDelaySeconds`      | `10`    | Seconds to delay before retrying a stuck post            |
| `MediaDownloadUrlExpirationMinutes` | `60`  | Media download URL expiration (image publishing)         |
| `VideoDownloadUrlExpirationMinutes` | `120` | Video download URL expiration (Facebook story videos)    |
| `ImagePollMaxAttempts`            | `30`    | Max polls for Instagram image container status           |
| `ImagePollIntervalSeconds`        | `2`     | Seconds between Instagram image container polls          |
| `OAuthStateExpirationMinutes`     | `10`    | OAuth state parameter expiration                         |

#### `Media` — Media handling settings

| Key                         | Default                  | Description                                     |
|-----------------------------|--------------------------|-------------------------------------------------|
| `UploadUrlExpirationMinutes`| `60`                     | Upload URL expiration (overridable via env var)  |
| `MaxImageFileSizeBytes`     | `20971520` (20 MB)       | Maximum image file size                          |
| `MaxVideoFileSizeBytes`     | `209715200` (200 MB)     | Maximum video file size                          |
| `LocalServerBaseUrl`        | `http://localhost:5122`  | Backend URL for local media serving              |

### File Layout

```
backend/
  appsettings.json                    # Base defaults (all environments)
  appsettings.Development.json        # ASP.NET Development overrides
  appsettings.Production.json         # ASP.NET Production overrides
  config/
    appsettings.common.json           # Non-secret config defaults (Meta API, Publishing, Media)
    appsettings.local.json            # Local development overrides
    appsettings.dev.json              # Staging/dev server overrides
    appsettings.prod.json             # Production overrides
```

### Secrets

No secrets should be stored in any `appsettings*.json` or `config/*.json` file. Use environment variables for:
- `DB_CONNECTION_STRING`
- `META_APP_ID`, `META_APP_SECRET`
- `GEMINI_API_KEY`

## Frontend Configuration

### Config Architecture

Frontend configuration uses JSON config files loaded at build time via `frontend/src/config/appConfig.ts`.

**Config precedence (last wins):**
1. `frontend/config/common.json` (shared defaults)
2. `frontend/config/{env}.json` (environment-specific, based on Vite `MODE`)
3. `VITE_API_URL` env var (optional override)

| Vite MODE     | Config file loaded         |
|---------------|----------------------------|
| `development` | `config/local.json`        |
| `staging`     | `config/dev.json`          |
| `production`  | `config/prod.json`         |

### Frontend Config Keys

| Key            | Default (local.json)         | Description            |
|----------------|------------------------------|------------------------|
| `apiBaseUrl`   | `http://localhost:5122/api`  | Backend API base URL   |

### VITE_API_URL Override

The `VITE_API_URL` environment variable can override `apiBaseUrl` from config files. Create a `.env` file in `frontend/`:

```
VITE_API_URL=https://api.yourapp.com/api
```

See `frontend/.env.example` for a template.

### File Layout

```
frontend/
  config/
    common.json     # Shared defaults (apiBaseUrl)
    local.json      # Local development config
    dev.json        # Staging config
    prod.json       # Production config
  src/
    config/
      appConfig.ts  # Config loader (merges common + env + VITE_ overrides)
```
