# Deployment Plan

End-to-end plan for deploying Post Pilot to live servers.

## 1. Architecture overview

Post Pilot ships two long-running containers built from a single multi-stage
[Dockerfile](../deploy/Dockerfile):

| Container | Stage | Purpose | Exposed port |
|---|---|---|---|
| `api` | `api` | ASP.NET Core (Kestrel) — controllers, runs EF Core migrations on startup | `5122` |
| `publisher` | `publisher` | Background `BackgroundService` that polls every 30s and publishes due posts to Meta | none |

External dependencies in production:

- **Managed PostgreSQL** (e.g. AWS RDS, GCP Cloud SQL, DO Managed DB, Neon, Supabase)
- **S3-compatible object storage** for media (AWS S3, Cloudflare R2, DO Spaces, Backblaze B2, Wasabi, MinIO)
- **Public HTTPS endpoint** that Meta can reach to fetch media during publishing

The frontend is a static Vite build served separately (CDN / nginx / object storage + CDN).

## 2. Run modes

The backend supports two run modes selected via `App__RunMode` (legacy: `APP_RUN_MODE`):

| Mode | Storage | Database | URLs |
|---|---|---|---|
| `local` | Local disk or local MinIO | Local Docker Postgres | `localhost` / ngrok tunnel |
| `server` | Managed S3-compatible bucket | Managed PostgreSQL | Production HTTPS domain |

Validation at startup ([backend/Settings/Validators/AppOptionsValidator.cs](../backend/Settings/Validators/AppOptionsValidator.cs)) rejects anything other than `local` or `server`.

## 3. Required environment variables

Production env file: [deploy/env/server.env](../deploy/env/server.env).

| Variable | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `APP_RUN_MODE` | `server` |
| `API_PORT` | Host port to expose (default `5122`) |
| `ASPNETCORE_URLS` | `http://0.0.0.0:5122` |
| `PUBLIC_URL` | Public HTTPS URL of the API (used by Meta to fetch media) |
| `META_APP_ID` / `META_APP_SECRET` | Facebook/Instagram OAuth app credentials |
| `GEMINI_API_KEY` | Google Gemini AI key |
| `GEMINI_MODEL` / `GEMINI_VISION_MODEL` | Gemini model selection |
| `ConnectionStrings__DefaultConnection` | Managed Postgres connection string (SSL required) |
| `MediaStorage__Provider` | `s3-compatible` |
| `MediaStorage__Bucket` | Bucket name |
| `MediaStorage__InternalEndpoint` | API/Worker → storage endpoint |
| `MediaStorage__PublicUploadEndpoint` | Endpoint signed into presigned URLs delivered to the browser |
| `MediaStorage__AccessKey` / `MediaStorage__SecretKey` | Storage credentials |
| `MediaStorage__UseSSL` | `true` in production |
| `MediaStorage__PresignedUploadExpirationMinutes` | Default `15` |

See [ENV.md](../ENV.md) for the full reference.

## 4. Pre-deployment checklist

- [ ] All secrets (`*.env`, credentials, `*.local.json`) excluded from the repository (see `.gitignore`)
- [ ] `deploy/env/server.env` filled in (not committed) — every `REPLACE_ME` replaced
- [ ] DNS A/AAAA record for the API domain points at the host
- [ ] TLS certificate provisioned (Let's Encrypt, ACM, Cloudflare, etc.)
- [ ] Managed Postgres provisioned, network reachable from the API container, SSL enforced
- [ ] S3-compatible bucket created, IAM/access-key scoped to that bucket
- [ ] Meta App configured with production redirect URI and `PUBLIC_URL` allowed
- [ ] Frontend built with the correct `VITE_API_URL` for the deployed API

## 5. Build & deploy

### 5.1 Build images

From `deploy/`:

```bash
docker compose --env-file ./env/server.env -f docker-compose.yml build
```

### 5.2 Bring up the stack

```bash
docker compose --env-file ./env/server.env -f docker-compose.yml up -d
```

Containers: `api` (port `5122`), `publisher` (no port). No local Postgres / MinIO — both are managed externally.

### 5.3 Verify

- `GET /swagger` (or any health-check route) returns 200 on the public URL
- `docker compose logs api` shows `PostPilot started — RunMode=server, PublicUrl=(set), …`
- `docker compose logs api` shows `Migrations applied successfully.`
- A test post with media schedules and publishes end-to-end

## 6. Migrations

EF Core migrations run automatically on API startup (see [backend/Program.cs:72-80](../backend/Program.cs#L72-L80)). The publisher does **not** run migrations.

For a major release, dry-run by generating an idempotent SQL script first:

```bash
dotnet ef migrations script --idempotent -p backend/PostPilot.Api.csproj
```

## 7. Backups & rollback

- **Database:** rely on managed Postgres automated backups + point-in-time recovery
- **Object storage:** enable versioning on the bucket so accidental deletes/overwrites are recoverable
- **App rollback:** keep prior image tags and redeploy by pinning `image:` in the compose file

## 8. Observability

- Container logs go to stdout (single-line, timestamped — see [backend/Program.cs:34-40](../backend/Program.cs#L34-L40))
- Ship them to the host's log aggregator (CloudWatch, Loki, Datadog, etc.)
- Toggle EF Core SQL logging at runtime via `Logging__EnableEfSql=true`

## 9. Security

- All inbound traffic must be HTTPS — terminate TLS at the reverse proxy / load balancer
- `Gemini__ApiKey` is sent via `x-goog-api-key` header (never in query strings) and not logged
- `META_APP_SECRET`, DB password, storage secret key — keep only in `server.env` (or a secret manager); never in source
- Backend secrets must never be exposed to the frontend
