# dev/ — Local development

Everything needed for local development of PostPilot. The full local stack
is api + worker + Postgres + pgAdmin + MinIO + an ngrok tunnel (so Meta can
reach the local API for OAuth + media fetch) + the Vite frontend.

## Files

| File | Purpose |
|---|---|
| [local.env.example](local.env.example) | Template for `deploy/env/local.env`. Copy + fill in Meta/Gemini credentials. |
| [scripts/start.ps1](scripts/start.ps1) | Full local startup — Docker stack, ngrok, App__PublicUrl patch, frontend, log tabs. |
| [scripts/stop.ps1](scripts/stop.ps1) | Stop everything `start.ps1` brought up. Pass `-PurgeData` to also wipe volumes. |
| [scripts/restart.ps1](scripts/restart.ps1) | Rebuild + restart api/worker only; leaves DB, MinIO, ngrok, frontend running. |
| [scripts/reset-db.ps1](scripts/reset-db.ps1) | Stop stack → delete `deploy_postgres_data` volume → restart (forces fresh EF migration). |
| [scripts/pgadmin-start.ps1](scripts/pgadmin-start.ps1) / [scripts/pgadmin-stop.ps1](scripts/pgadmin-stop.ps1) | Standalone pgAdmin (useful when the DB lives elsewhere, e.g. Supabase). |

## Where local Docker still lives

The compose files and Dockerfile for the local stack remain under
[deploy/](../deploy/):

| File | Why it stays in deploy/ |
|---|---|
| [deploy/Dockerfile](../deploy/Dockerfile) | Single multi-stage Dockerfile with `--target api` and `--target publisher`. Used by both the local stack and the future GitHub Actions production build, so it sits at a neutral path. |
| [deploy/docker-compose.yml](../deploy/docker-compose.yml) + [deploy/docker-compose.local.db.yml](../deploy/docker-compose.local.db.yml) + [deploy/docker-compose.local.storage.yml](../deploy/docker-compose.local.storage.yml) | Compose project name defaults to the parent folder. The scripts in this folder hardcode `deploy-api-1` / `deploy-publisher-1` as container names when tailing logs in Windows Terminal tabs. Renaming the folder would break that. |
| [deploy/env/local.env](../deploy/env/local.env) | Referenced by absolute path inside the local compose stack and as `--env-file ./env/local.env` in every script in this folder. Gitignored — real credentials only. |

If you ever want to finish moving the compose files into `dev/`: set
`COMPOSE_PROJECT_NAME=postpilot` in the env file, drop the `deploy-` prefix
from the log-tab container names in [scripts/start.ps1](scripts/start.ps1)
and [scripts/restart.ps1](scripts/restart.ps1), then `git mv` the compose
files + env.

## Quick start

```powershell
# First time only: create the local env file from the template
cp dev/local.env.example deploy/env/local.env
# then edit deploy/env/local.env to set META_APP_ID, META_APP_SECRET, GEMINI_API_KEY

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
