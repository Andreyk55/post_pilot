# VPS Deployment Plan

Deploy the Post Pilot **backend** (api + publisher + database + media storage)
to a single VPS as Docker containers. The frontend lives separately on Vercel
and is not covered here.

## 1. Architecture

Five containers on one host, on a single docker-compose network:

| Container | Image | Port (host) | Role |
|---|---|---|---|
| `nginx` | `nginx:1.27-alpine` | 80 (and 443 later) | Reverse proxy in front of everything |
| `api` | built from `deploy/Dockerfile` | — | ASP.NET Core, runs EF migrations on startup |
| `publisher` | built from `deploy/Dockerfile` | — | Background worker, polls and publishes due posts |
| `postgres` | `postgres:16` | — | Database. Volume: `postgres_data` |
| `minio` | `minio/minio` | — | S3-compatible media storage. Volume: `minio_data` |
| `minio-init` | `minio/mc` | — | One-shot job that creates the bucket |

Only nginx is published to the public internet. Everything else is reachable
only across the compose network.

Public routes (HTTP for now):

| Path | Routes to |
|---|---|
| `/` | `api:5122` (controllers, Swagger) |
| `/storage/` | `minio:9000` (S3 API — used by browser presigned uploads) |
| `/storage-console/` | `minio:9001` (MinIO web UI) |

## 2. Files in this repo

| File | Purpose |
|---|---|
| [deploy/docker-compose.yml](../deploy/docker-compose.yml) | Base `api` + `publisher` services |
| [deploy/docker-compose.server.db.yml](../deploy/docker-compose.server.db.yml) | Adds postgres + healthcheck-gated `depends_on` |
| [deploy/docker-compose.server.storage.yml](../deploy/docker-compose.server.storage.yml) | Adds minio + `minio-init` |
| [deploy/docker-compose.server.proxy.yml](../deploy/docker-compose.server.proxy.yml) | Adds nginx |
| [deploy/nginx/postpilot.conf](../deploy/nginx/postpilot.conf) | nginx routing config |
| [deploy/env/server.env](../deploy/env/server.env) | All env vars (fill in before deploy) |
| [scripts/server-start.sh](../scripts/server-start.sh) | Bring up the whole stack |
| [scripts/server-stop.sh](../scripts/server-stop.sh) | Take it down (keeps volumes) |

## 3. Pre-flight on the VPS

1. Install Docker Engine + the `docker compose` plugin.
2. Clone the repo (or `scp` it).
3. Open port 80 (and later 443) in the firewall:
   ```bash
   sudo ufw allow 80/tcp
   ```
4. Decide on the public hostname:
   - If you have a domain → point an A record at the VPS IP.
   - If not → just use the raw IP. Meta OAuth/publishing won't work until
     you have HTTPS, but the API itself will respond.

## 4. Fill in `deploy/env/server.env`

Every `REPLACE_ME`, `CHANGE_ME_*`, `VPS_PUBLIC_HOST`, and
`YOUR_VERCEL_DOMAIN` must be replaced. Key fields:

| Variable | What to set |
|---|---|
| `App__PublicUrl` | `http://<vps-ip-or-domain>` — Meta uses this to fetch media |
| `Meta__RedirectUri` | Your Vercel frontend's OAuth callback URL |
| `META_APP_ID` / `META_APP_SECRET` | From the Meta developer console |
| `GEMINI_API_KEY` | Google AI Studio key |
| `POSTGRES_PASSWORD` | Strong password (also update it inside `ConnectionStrings__DefaultConnection`) |
| `MINIO_ROOT_PASSWORD` | Strong password (also update it inside `MediaStorage__SecretKey`) |
| `MINIO_API_CORS_ALLOW_ORIGIN` | The exact Vercel origin(s) the frontend uses |
| `MediaStorage__PublicUploadEndpoint` | `http://<vps-ip-or-domain>/storage` |

`server.env` should NEVER be committed. It's already ignored via the
`*.env` rule in [.gitignore](../.gitignore).

## 5. Start the stack

From the repo root on the VPS:

```bash
chmod +x scripts/server-start.sh scripts/server-stop.sh
./scripts/server-start.sh
```

The script:
1. Verifies the env file has no placeholders left
2. Builds the api + publisher images
3. Brings everything up
4. Waits up to 120s for the API to answer `/api/media/constraints`

## 6. Verify

- `curl http://<vps-host>/api/media/constraints` → 200 with a JSON body
- `docker compose ... logs api` shows `Migrations applied successfully.`
- `docker compose ... logs publisher` shows the worker polling every 30s
- `http://<vps-host>/storage-console/` opens the MinIO UI (login with `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD`)
- Create a post from the Vercel frontend and watch publisher logs

## 7. Updating the deploy

```bash
git pull
./scripts/server-start.sh   # rebuilds api/publisher and re-applies compose
```

EF Core migrations run on every API startup, so schema changes ship
automatically with a `docker compose up -d --build`.

## 8. Backups

- **Postgres:** the `postgres_data` named volume holds all DB state. Back it
  up with `docker exec postpilot-db pg_dump -U postpilot postpilot | gzip > backup.sql.gz`
  on a cron.
- **MinIO:** the `minio_data` volume holds uploaded media. Either snapshot the
  volume directly or `docker run --rm -v minio_data:/data ...` to copy it out.

## 9. Going to HTTPS (when you're ready)

This deploy ships plain HTTP because there's no domain and no certificate yet.
Mixed-content rules in browsers will prevent the HTTPS Vercel frontend from
calling this plain-HTTP API, so right now only `curl`/Postman work end-to-end.

To switch to HTTPS:

1. Get a domain and point it at the VPS IP.
2. On the VPS, install certbot and issue a cert for that domain:
   ```bash
   sudo certbot certonly --standalone -d api.yourdomain.com
   ```
   Then copy `fullchain.pem` and `privkey.pem` into `deploy/nginx/certs/`.
3. Uncomment the `server { listen 443 ssl ... }` block in
   [deploy/nginx/postpilot.conf](../deploy/nginx/postpilot.conf).
4. In `server.env`:
   - Flip `App__EnableHttpsRedirect=true`
   - Change `App__PublicUrl` to `https://api.yourdomain.com`
   - Change `MediaStorage__PublicUploadEndpoint` to `https://api.yourdomain.com/storage`
   - Change `MediaStorage__UseSSL=true`
5. `./scripts/server-start.sh` to restart.

## 10. Frontend (Vercel) configuration

In your Vercel project's environment variables, set the API base URL to
`http://<vps-host>` (or the HTTPS variant once you have it). The frontend
makes one CORS request per API call, and presigned upload PUTs go directly
to `/storage/...` on the same host — both work as long as the Vercel origin
is in `MINIO_API_CORS_ALLOW_ORIGIN` on the server.
