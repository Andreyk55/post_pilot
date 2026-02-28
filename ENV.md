# Environment Variables

## Backend (Startup.cs)

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `APP_RUN_MODE` | Yes | — | `local` or `server` — controls media storage strategy |
| `META_APP_ID` | Yes | — | Facebook/Instagram OAuth app ID |
| `META_APP_SECRET` | Yes | — | Facebook/Instagram OAuth app secret |
| `GEMINI_API_KEY` | Yes | — | Google Gemini AI API key |
| `GEMINI_MODEL` | Yes | — | Gemini model name (e.g. `gemini-2.0-flash`) |
| `GEMINI_VISION_MODEL` | No | Falls back to `GEMINI_MODEL` | Separate model for image analysis |
| `PUBLIC_URL` | No | `LocalServerBaseUrl` from config | Public base URL (for ngrok/tunneling) |

## Framework

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | No | `Production` | Controls which `appsettings.{env}.json` loads |

## Frontend (vite.config.ts)

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `VITE_API_URL` | No | `http://localhost:5122` | Backend API base URL |
