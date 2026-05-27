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
