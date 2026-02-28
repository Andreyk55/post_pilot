# Environment Variables

## Overview

PostPilot uses standard .NET configuration with `__`-separated environment variable names
that map to hierarchical config keys (e.g. `Gemini__ApiKey` → `Gemini:ApiKey`).

Flat env var names (e.g. `GEMINI_API_KEY`) are also supported via `EnvVarMapper`
but are **deprecated** and will be removed in a future release.

## Backend — Canonical env vars (preferred)

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `App__RunMode` | Yes | `local` | `local` or `server` — controls media storage strategy |
| `App__PublicUrl` | No | `LocalServerBaseUrl` from config | Public base URL (for ngrok/tunneling) |
| `Meta__AppId` | Yes | — | Facebook/Instagram OAuth app ID |
| `Meta__AppSecret` | Yes | — | Facebook/Instagram OAuth app secret |
| `Meta__RedirectUri` | Yes | from config | OAuth redirect URI |
| `Gemini__ApiKey` | Yes | — | Google Gemini AI API key |
| `Gemini__Model` | Yes | — | Gemini model name (e.g. `gemini-2.0-flash`) |
| `Gemini__VisionModel` | No | Falls back to `Gemini__Model` | Separate model for image analysis |

## Backend — Legacy env vars (deprecated, backward compat only)

These flat names are mapped to canonical keys at startup. Prefer the `__` names above.

| Legacy Variable | Maps To |
|---|---|
| `APP_RUN_MODE` | `App__RunMode` |
| `PUBLIC_URL` | `App__PublicUrl` |
| `META_APP_ID` | `Meta__AppId` |
| `META_APP_SECRET` | `Meta__AppSecret` |
| `GEMINI_API_KEY` | `Gemini__ApiKey` |
| `GEMINI_MODEL` | `Gemini__Model` |
| `GEMINI_VISION_MODEL` | `Gemini__VisionModel` |

If both legacy and canonical env vars are set, the canonical one wins.

## Framework

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | No | `Production` | Controls which `appsettings.{env}.json` loads |

## Frontend (vite.config.ts)

| Variable | Required | Default | Purpose |
|---|---|---|---|
| `VITE_API_URL` | No | `http://localhost:5122` | Backend API base URL (**build-time only** — baked into the JS bundle) |

> **Note:** `VITE_` variables are embedded at build time by Vite. Never put backend secrets
> in `VITE_` variables — they will be visible in the browser.

## How .NET config binding works

.NET configuration uses a hierarchical key model. Environment variables override config files:
- Config key `Gemini:ApiKey` can be set via env var `Gemini__ApiKey` (double underscore = section separator).
- `AddEnvironmentVariables()` automatically handles this mapping.
- The `EnvVarMapper` in `Program.cs` additionally maps flat names (e.g. `GEMINI_API_KEY`) into canonical keys.

## Security

- The Gemini API key is sent via the `x-goog-api-key` HTTP header (never in URL query strings).
- Sensitive headers (`x-goog-api-key`, `Authorization`) are not logged.
- Backend secrets (`Meta__AppSecret`, `Gemini__ApiKey`) must never be exposed to the frontend.
