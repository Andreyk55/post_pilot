# prod/ — Production deployment

Everything in this folder is for the **Hetzner VPS** running the PostPilot
backend (API + worker + Postgres). The frontend is on Vercel and is not
managed from here.

For the full step-by-step runbook see [docs/deployment-vps.md](../docs/deployment-vps.md).

## Files

| File | Purpose |
|---|---|
| [docker-compose.yml](docker-compose.yml) | Stack: `postpilot-api`, `postpilot-worker`, `postpilot-minio`, plus `postpilot-postgres` under the `localdb` compose profile. Pulls images from GHCR. |
| [server.local.env.example](server.local.env.example) | Template for local-Postgres mode. Copy to `/opt/postpilot/server.env` and start with `--profile localdb`. |
| [server.supabase.env.example](server.supabase.env.example) | Template for Supabase mode. Copy to `/opt/postpilot/server.env` and start without the profile flag. |
| [nginx/postpilot-api.conf](nginx/postpilot-api.conf) | Host nginx config: TLS termination + reverse proxy to `127.0.0.1:5122`. |
| [scripts/deploy.sh](scripts/deploy.sh) | `pull` + `up -d` + `ps`. Run on the VPS by GitHub Actions. |
| [scripts/check-prod.sh](scripts/check-prod.sh) | Smoke test: container status, `/health` (local + public), port bindings. |

## File layout on the VPS

```
/opt/postpilot/
├── server.env                        ← REAL secrets (not in git, chmod 600)
└── prod/
    ├── docker-compose.yml
    ├── nginx/
    │   └── postpilot-api.conf        ← copied to /etc/nginx/sites-available/
    └── scripts/
        ├── deploy.sh
        └── check-prod.sh
```

`server.env` deliberately lives **above** `prod/`. The compose file references
it via the absolute path `/opt/postpilot/server.env`, never via a relative
path inside the repo.

## First-time setup on the VPS

```bash
# 1. Install Docker
curl -fsSL https://get.docker.com | sudo sh

# 2. Layout
sudo mkdir -p /opt/postpilot
sudo chown $USER:$USER /opt/postpilot

# 3. Copy this folder from your laptop (or clone the repo and symlink)
scp -r prod vps:/opt/postpilot/

# 4. Create the real secrets file from one of the two templates
# Pick ONE depending on which DB you want (see docs/deployment-vps.md §3):
cp /opt/postpilot/prod/server.local.env.example    /opt/postpilot/server.env   # local Docker Postgres
# OR
cp /opt/postpilot/prod/server.supabase.env.example /opt/postpilot/server.env   # Supabase

chmod 600 /opt/postpilot/server.env
nano /opt/postpilot/server.env    # replace every CHANGE_ME

# 5. Replace the GHCR owner placeholder in docker-compose.yml
sed -i 's|<github-owner>|YOUR_GH_OWNER|g' /opt/postpilot/prod/docker-compose.yml

# 6. Install the nginx site
sudo cp /opt/postpilot/prod/nginx/postpilot-api.conf /etc/nginx/sites-available/
sudo ln -sf /etc/nginx/sites-available/postpilot-api.conf /etc/nginx/sites-enabled/
sudo certbot --nginx -d post-pilot.cloud-ip.cc
sudo nginx -t && sudo systemctl reload nginx

# 7. First deploy
chmod +x /opt/postpilot/prod/scripts/*.sh
/opt/postpilot/prod/scripts/deploy.sh
```

## Day-to-day commands

```bash
# Deploy a new build (after GitHub Actions pushed new images)
/opt/postpilot/prod/scripts/deploy.sh

# Smoke check
/opt/postpilot/prod/scripts/check-prod.sh

# Logs
docker compose -f /opt/postpilot/prod/docker-compose.yml logs -f postpilot-api
docker compose -f /opt/postpilot/prod/docker-compose.yml logs -f postpilot-worker

# Shell into Postgres
docker compose -f /opt/postpilot/prod/docker-compose.yml exec postpilot-postgres \
    psql -U postpilot postpilot
```

## What lives where

- **Migrations** run on `postpilot-api` startup ([backend/Program.cs](../backend/Program.cs)). The worker explicitly does NOT run them ([backend/publisher/Program.cs](../backend/publisher/Program.cs)).
- **Port exposure**: only `127.0.0.1:5122` is bound. Postgres has no host port at all.
- **CORS**: allowlist comes from `Cors__AllowedOrigins__*` env vars. Production allows only `https://post-auto-pilot.vercel.app`.
- **Forwarded headers**: API trusts `X-Forwarded-For` + `X-Forwarded-Proto` from any proxy IP, since nginx and the API share localhost.
