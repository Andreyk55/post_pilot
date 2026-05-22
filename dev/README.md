# dev/ — Local development helpers

Templates, wrapper scripts, and docs for the local Docker stack
(api + worker + Postgres + pgAdmin + MinIO, with an ngrok tunnel for Meta
to reach the API).

## Why the heavy files still live elsewhere

A clean reorg would put the local compose files under `dev/` too. We
deliberately didn't — and this section explains why so the next person
doesn't re-litigate it.

| File | Why it stayed |
|---|---|
| [deploy/Dockerfile](../deploy/Dockerfile) | Single multi-stage Dockerfile with named targets `api` and `publisher`. Used by both local builds and the GitHub Actions production image build. Moving it would touch every existing compose file AND the future CI workflow. |
| [deploy/docker-compose.yml](../deploy/docker-compose.yml) + [deploy/docker-compose.local.db.yml](../deploy/docker-compose.local.db.yml) + [deploy/docker-compose.local.storage.yml](../deploy/docker-compose.local.storage.yml) | Compose project name defaults to the parent folder, so containers are named `deploy-api-1` and `deploy-publisher-1`. The PowerShell scripts hardcode those names when tailing logs in Windows Terminal tabs ([scripts/start.ps1](../scripts/start.ps1), see `containerName = "deploy-$svc-1"`). Renaming the folder breaks the log tabs and the restart code path. |
| [deploy/env/local.env](../deploy/env/local.env) | Referenced by absolute path inside the local compose stack and by `--env-file ./env/local.env` in the start/stop scripts. |
| [scripts/start.ps1](../scripts/start.ps1) / [scripts/stop.ps1](../scripts/stop.ps1) / [scripts/restart.ps1](../scripts/restart.ps1) / [scripts/pgadmin-*.ps1](../scripts/) | Same — they reference the deploy/ paths above. |

If you ever want to finish the move, the work is: rename the compose
project (e.g. via `COMPOSE_PROJECT_NAME=postpilot` in the env file), retire
the `deploy-` prefix in the log-tab code, and move the four compose files
+ env into `dev/`.

## Files in this folder

| File | Purpose |
|---|---|
| [local.env.example](local.env.example) | Template for `deploy/env/local.env`. Copy + fill in your own Meta/Gemini credentials. |
| [scripts/start-local.ps1](scripts/start-local.ps1) | Thin wrapper → `scripts/start.ps1`. |
| [scripts/stop-local.ps1](scripts/stop-local.ps1) | Thin wrapper → `scripts/stop.ps1`. |
| [scripts/reset-db.ps1](scripts/reset-db.ps1) | Stop stack, delete Postgres volume, restart (forces a clean EF migration). |

## Quick start

```powershell
# First time only: create the local env file from the template
cp dev/local.env.example deploy/env/local.env
# then edit deploy/env/local.env to set META_APP_ID, META_APP_SECRET, GEMINI_API_KEY

# Start everything (Docker stack + ngrok + Vite + log tabs)
pwsh -File dev/scripts/start-local.ps1

# Stop everything
pwsh -File dev/scripts/stop-local.ps1

# Wipe the local DB and re-run migrations
pwsh -File dev/scripts/reset-db.ps1
```

After `start-local.ps1` you should see:

- Frontend: <http://localhost:5173>
- API: <http://localhost:5122> (Swagger at `/swagger`)
- pgAdmin: <http://localhost:5050>
- MinIO console: <http://localhost:9001>
- ngrok inspector: <http://localhost:4040>

## Production deployment is separate

For VPS / GHCR / nginx production deployment see [prod/](../prod/README.md)
and [docs/deployment-vps.md](../docs/deployment-vps.md).
