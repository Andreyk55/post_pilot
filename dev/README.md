# dev/ — Local development

Everything needed for local development of PostPilot. The full local stack
is api + worker + Postgres + pgAdmin + MinIO + an ngrok tunnel (so Meta can
reach the local API for OAuth + media fetch) + the Vite frontend.

## Files

| File | Purpose |
|---|---|
| [docker-compose.yml](docker-compose.yml) | Base local stack: builds `api` + `publisher` from source via [../build/Dockerfile](../build/Dockerfile). |
| [docker-compose.local.db.yml](docker-compose.local.db.yml) | Overlay: Postgres + pgAdmin, healthcheck-gated `depends_on`. |
| [docker-compose.local.storage.yml](docker-compose.local.storage.yml) | Overlay: MinIO + one-shot bucket init. |
| [local.env.example](local.env.example) | Template for `dev/local.env`. Copy + fill in Meta/Gemini credentials. |
| [scripts/start.ps1](scripts/start.ps1) | Full local startup — Docker stack, ngrok, App__PublicUrl patch, frontend, log tabs. |
| [scripts/stop.ps1](scripts/stop.ps1) | Stop everything `start.ps1` brought up. Pass `-PurgeData` to also wipe volumes. |
| [scripts/restart.ps1](scripts/restart.ps1) | Rebuild + restart api/worker only; leaves DB, MinIO, ngrok, frontend running. |
| [scripts/reset-db.ps1](scripts/reset-db.ps1) | Stop stack → delete `postpilot_postgres_data` volume → restart (forces fresh EF migration). |
| [scripts/pgadmin-start.ps1](scripts/pgadmin-start.ps1) / [scripts/pgadmin-stop.ps1](scripts/pgadmin-stop.ps1) | Standalone pgAdmin (useful when the DB lives elsewhere, e.g. Supabase). |

## Where the shared Dockerfile lives

The Dockerfile is at [../build/Dockerfile](../build/Dockerfile) — a neutral
top-level location because it is built by both:

- This local stack (`docker-compose.yml` build context `../backend`, dockerfile `../build/Dockerfile`).
- The future GitHub Actions production workflow (`--target api` / `--target publisher`, pushed to GHCR).

## Container naming

Container names start with `postpilot-` (e.g. `postpilot-api-1`,
`postpilot-publisher-1`). The prefix comes from `COMPOSE_PROJECT_NAME=postpilot`
set inside `local.env`, **not** from the folder name. The log-tab code in
`start.ps1` / `restart.ps1` references these by name.

## Quick start

```powershell
# First time only: create the local env file from the template
cp dev/local.env.example dev/local.env
# then edit dev/local.env to set META_APP_ID, META_APP_SECRET, GEMINI_API_KEY

# Start everything (Docker stack + ngrok + Vite + log tabs)
pwsh -File dev/scripts/start.ps1

# Stop everything
pwsh -File dev/scripts/stop.ps1

# Rebuild api/worker only (after backend code changes)
pwsh -File dev/scripts/restart.ps1

# Wipe the local DB and re-run migrations from scratch
pwsh -File dev/scripts/reset-db.ps1
```

After `start.ps1` you should see:

- Frontend: <http://localhost:5173>
- API: <http://localhost:5122> (Swagger at `/swagger`)
- pgAdmin: <http://localhost:5050>
- MinIO console: <http://localhost:9001>
- ngrok inspector: <http://localhost:4040>

## Production deployment is separate

For VPS / GHCR / nginx production deployment see [prod/](../prod/README.md)
and [docs/deployment-vps.md](../docs/deployment-vps.md).
