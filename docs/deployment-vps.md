# Production Deployment — Hetzner VPS

PostPilot's backend (API + worker + Postgres) runs on a single Hetzner VPS as
Docker containers. The frontend lives on Vercel at
<https://post-auto-pilot.vercel.app> and is **not** covered here.

## Architecture

```
                  internet
                     │
                     ▼
        host nginx (TLS, on the VPS itself)
                     │  proxy_pass
                     ▼
            127.0.0.1:5122  ──►  postpilot-api ────┐
                                                   │  service network
                                  postpilot-worker │  (no host ports)
                                                   │
                                  postpilot-postgres ◄─┘
                                  (volume: postpilot-postgres-data)
```

| Service | Image | Host port | Role |
|---|---|---|---|
| `postpilot-api` | `ghcr.io/<github-owner>/postpilot-api:latest` | `127.0.0.1:5122` | ASP.NET Core API. Runs EF Core migrations on startup. |
| `postpilot-worker` | `ghcr.io/<github-owner>/postpilot-worker:latest` | — | Background publishing loop. Does NOT run migrations. |
| `postpilot-postgres` | `postgres:16` | — (compose-network only) | Database. Volume: `postpilot-postgres-data`. |

Only port 5122 is published, and **only on `127.0.0.1`** — the public internet
cannot reach it directly. Host nginx terminates HTTPS for
`https://post-pilot.cloud-ip.cc` and proxies to `http://127.0.0.1:5122`.

Postgres has no host port at all. Run `psql` via
`docker compose exec postpilot-postgres psql -U postpilot postpilot`.

## 1. One-time VPS setup

```bash
# Install Docker Engine + the compose plugin (Ubuntu 24.04+)
curl -fsSL https://get.docker.com | sudo sh

# Folder that holds the compose file + secrets
sudo mkdir -p /opt/postpilot
sudo chown $USER:$USER /opt/postpilot
```

Place `docker-compose.prod.yml` in `/opt/postpilot/`. Either copy it from the
repo (`scp deploy/docker-compose.prod.yml vps:/opt/postpilot/`) or clone the
repo on the VPS and symlink it. GitHub Actions will keep the file in sync
later.

## 2. Create the secrets file

```bash
# From the repo
scp deploy/server.env.example vps:/opt/postpilot/server.env
# On the VPS
sudo nano /opt/postpilot/server.env   # fill in every CHANGE_ME
sudo chmod 600 /opt/postpilot/server.env
```

Fields you **must** set:

| Variable | Value |
|---|---|
| `POSTGRES_PASSWORD` | Strong random password |
| `ConnectionStrings__DefaultConnection` | Same password as `POSTGRES_PASSWORD` |
| `Meta__AppId` / `Meta__AppSecret` (+ `META_APP_ID` / `META_APP_SECRET`) | From Meta developer console |
| `Gemini__ApiKey` (+ `GEMINI_API_KEY`) | Google AI Studio key |

`/opt/postpilot/server.env` is **never** committed. The repo's `.gitignore`
covers `*.env`, `deploy/env/server.env`, `deploy/server.env`, and `server.env`.

## 3. Start the stack

```bash
cd /opt/postpilot
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
```

The API container runs EF Core migrations on startup. The worker waits for
the API to be up (via `depends_on`) and never runs migrations — so they
cannot race or double-apply.

## 4. Verify

```bash
# Containers up?
docker compose -f docker-compose.prod.yml ps

# API logs (look for "Migrations applied successfully.")
docker compose -f docker-compose.prod.yml logs -f postpilot-api

# Worker logs (look for "PostPilot.Publisher started")
docker compose -f docker-compose.prod.yml logs -f postpilot-worker

# Local health check (loopback only — nginx uses this same URL)
curl -i http://127.0.0.1:5122/health

# Public health check via host nginx → HTTPS
curl -i https://post-pilot.cloud-ip.cc/health

# Confirm port bindings: 5122 should be 127.0.0.1 only, 5432 absent
sudo ss -tulpn | grep -E '5122|5432'
```

Expected `ss` output:

```
LISTEN  0   ...  127.0.0.1:5122   ...   docker-proxy
# (no row for 5432 — Postgres is not bound to any host interface)
```

If you see `0.0.0.0:5122` or any row for `5432`, **stop and fix** before the
DNS record propagates.

## 5. Host nginx configuration

Host nginx (not containerised in this layout) terminates TLS and proxies to
the API. Minimal server block:

```nginx
server {
    listen 443 ssl http2;
    server_name post-pilot.cloud-ip.cc;

    ssl_certificate     /etc/letsencrypt/live/post-pilot.cloud-ip.cc/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/post-pilot.cloud-ip.cc/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:5122;
        proxy_set_header Host              $host;
        proxy_set_header X-Real-IP         $remote_addr;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 60s;
    }
}

server {
    listen 80;
    server_name post-pilot.cloud-ip.cc;
    return 301 https://$host$request_uri;
}
```

The API trusts `X-Forwarded-For` and `X-Forwarded-Proto` (see
[backend/Startup.cs:60-70](../backend/Startup.cs#L60-L70)).

## 6. Updating

When GitHub Actions pushes new images to GHCR:

```bash
cd /opt/postpilot
docker compose -f docker-compose.prod.yml pull
docker compose -f docker-compose.prod.yml up -d
```

This is the workflow's deploy step (image tags can be pinned via
`API_IMAGE` / `WORKER_IMAGE` env overrides on the compose command if you
prefer immutable SHA tags over `:latest`).

## 7. Backups

```bash
# Postgres dump (run on the VPS, e.g. via cron)
docker compose -f /opt/postpilot/docker-compose.prod.yml exec -T postpilot-postgres \
    pg_dump -U postpilot postpilot | gzip > /var/backups/postpilot-$(date +%F).sql.gz
```

The data lives in the `postpilot-postgres-data` named volume. `docker
compose down` keeps it; `docker compose down -v` deletes it.

## 8. Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /health` | Cheap liveness probe — no DB call. Used by nginx upstream health and uptime monitoring. |
| `GET /api/internal/health` | Existing health endpoint inside the API surface. |

## 9. Local development

This file changes nothing about local dev. Continue using:

```bash
# Local stack (Postgres + API + worker + pgAdmin, builds from source)
./scripts/start.ps1    # Windows
```

The local compose stack uses `deploy/docker-compose.yml` + the `local.*`
overlays and `deploy/env/local.env`. Frontend still points at
`VITE_API_URL=http://localhost:5122`.
