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
        prod/nginx/postpilot-api.conf
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

## Repository layout (what lives where)

```
prod/                                ← committed
├── docker-compose.yml               ← 3-service stack, pulls GHCR images
├── server.env.example               ← safe template
├── nginx/postpilot-api.conf         ← host nginx config template
├── scripts/deploy.sh                ← pull + up -d + ps
├── scripts/check-prod.sh            ← smoke check
└── README.md

docs/
└── deployment-vps.md                ← this file

.github/workflows/
└── deploy-prod.yml                  ← (to be added — see §7)
```

On the VPS the files land under `/opt/postpilot/`:

```
/opt/postpilot/
├── server.env                       ← REAL secrets — never in git
└── prod/                            ← copied from this repo
    ├── docker-compose.yml
    ├── nginx/postpilot-api.conf
    └── scripts/{deploy.sh, check-prod.sh}
```

`server.env` deliberately sits **outside** `prod/`. The compose file points at
`/opt/postpilot/server.env` by absolute path so the secret file never lives
inside the repo path on the VPS either.

## 1. One-time VPS setup

```bash
# Install Docker Engine + the compose plugin (Ubuntu 24.04+)
curl -fsSL https://get.docker.com | sudo sh

# Layout
sudo mkdir -p /opt/postpilot
sudo chown $USER:$USER /opt/postpilot
```

## 2. Copy the prod/ folder to the VPS

From your laptop, working from the repo root:

```bash
# Whole prod/ tree to /opt/postpilot/prod/
scp -r prod vps:/opt/postpilot/

# Or, if you keep a git clone on the VPS, symlink instead:
# ssh vps "ln -sf /home/USER/post_pilot/prod /opt/postpilot/prod"
```

## 3. Create the real secrets file

```bash
# On the VPS
cp /opt/postpilot/prod/server.env.example /opt/postpilot/server.env
chmod 600 /opt/postpilot/server.env
nano /opt/postpilot/server.env   # replace every CHANGE_ME
```

Fields you **must** set:

| Variable | Value |
|---|---|
| `POSTGRES_PASSWORD` | Strong random password |
| `ConnectionStrings__DefaultConnection` | Same password as `POSTGRES_PASSWORD` |
| `Meta__AppId` / `Meta__AppSecret` (+ `META_APP_ID` / `META_APP_SECRET`) | From Meta developer console |
| `Gemini__ApiKey` (+ `GEMINI_API_KEY`) | Google AI Studio key |

`server.env` is **never** committed. The repo's `.gitignore` covers `*.env`,
`prod/server.env`, `dev/local.env`, and `server.env` everywhere.

## 4. Replace the GHCR owner placeholder

`prod/docker-compose.yml` ships with `<github-owner>` literals. Either:

```bash
# Edit in place once on the VPS
sed -i 's|<github-owner>|YOUR_GH_OWNER|g' /opt/postpilot/prod/docker-compose.yml
```

…or override per-invocation via env: `API_IMAGE=ghcr.io/...:sha-abc docker compose ...`.

## 5. Install host nginx

```bash
sudo cp /opt/postpilot/prod/nginx/postpilot-api.conf /etc/nginx/sites-available/
sudo ln -sf /etc/nginx/sites-available/postpilot-api.conf /etc/nginx/sites-enabled/

# Issue a certificate (Certbot's --nginx plugin auto-edits the server block;
# the template ships with the standard Let's Encrypt paths already wired in)
sudo certbot --nginx -d post-pilot.cloud-ip.cc

sudo nginx -t && sudo systemctl reload nginx
```

## 6. Start the stack

```bash
chmod +x /opt/postpilot/prod/scripts/*.sh
/opt/postpilot/prod/scripts/deploy.sh
```

`deploy.sh` runs:

```bash
cd /opt/postpilot
docker compose -f prod/docker-compose.yml pull
docker compose -f prod/docker-compose.yml up -d
docker compose -f prod/docker-compose.yml ps
```

The API container runs EF Core migrations on startup. The worker waits for
the API to be up (via `depends_on`) and never runs migrations — so they
cannot race or double-apply.

## 7. Verify

```bash
# Containers up?
docker compose -f /opt/postpilot/prod/docker-compose.yml ps

# API logs (look for "Migrations applied successfully.")
docker compose -f /opt/postpilot/prod/docker-compose.yml logs -f postpilot-api

# Worker logs (look for "PostPilot.Publisher started")
docker compose -f /opt/postpilot/prod/docker-compose.yml logs -f postpilot-worker

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

Or just run the bundled smoke check:

```bash
/opt/postpilot/prod/scripts/check-prod.sh
```

## 8. Updating (manual)

```bash
/opt/postpilot/prod/scripts/deploy.sh
```

GitHub Actions will eventually run this for you over SSH — see §10.

## 9. Backups

```bash
# Postgres dump (run on the VPS, e.g. via cron)
docker compose -f /opt/postpilot/prod/docker-compose.yml exec -T postpilot-postgres \
    pg_dump -U postpilot postpilot | gzip > /var/backups/postpilot-$(date +%F).sql.gz
```

Data lives in the `postpilot-postgres-data` named volume. `docker compose
down` keeps it; `docker compose down -v` deletes it.

## 10. Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /health` | Cheap liveness probe — no DB call. Used by nginx upstream health and uptime monitoring. |
| `GET /api/internal/health` | Existing health endpoint inside the API surface (returns the same shape). |

## 11. GitHub Actions (to be added)

Add the workflow at `.github/workflows/deploy-prod.yml` (GitHub requires
that exact path). The build/push job should:

- Build the image with `docker build --target api -t ghcr.io/<owner>/postpilot-api:<tag>` (and the same for `--target publisher` → `postpilot-worker`).
- Use the shared `build/Dockerfile` at the repo root as the build context root.
- Push to GHCR.
- SSH to the VPS and run `/opt/postpilot/prod/scripts/deploy.sh`.

Paths the workflow will need to know:
- Dockerfile: [build/Dockerfile](../build/Dockerfile)
- Build context: `backend/`
- Compose file (on VPS): `/opt/postpilot/prod/docker-compose.yml`
- Deploy script: `/opt/postpilot/prod/scripts/deploy.sh`

## 12. Local development is separate

This file changes nothing about local dev. See [dev/README.md](../dev/README.md)
for the local stack (Docker + ngrok + Vite). Frontend continues to point at
`VITE_API_URL=http://localhost:5122`.
