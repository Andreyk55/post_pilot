# Environment Variables

## Required (env vars only — never in appsettings)

| Variable | Purpose |
|---|---|
| `App__RunMode` | `local` or `server` — controls media storage strategy |
| `Meta__AppId` | Facebook/Instagram OAuth app ID |
| `Meta__AppSecret` | Facebook/Instagram OAuth app secret |
| `Gemini__ApiKey` | Google Gemini AI API key |

Legacy flat names (`APP_RUN_MODE`, `META_APP_ID`, `META_APP_SECRET`, `GEMINI_API_KEY`)
are still accepted for backward compatibility.

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
