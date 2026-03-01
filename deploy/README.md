# PostPilot – Docker Compose Deployment

## Overview

| File | Purpose |
|---|---|
| `docker-compose.yml` | **Base** – API service only. Used in both local and server. |
| `docker-compose.local.db.yml` | **Local only** – Postgres + pgAdmin overlay. |
| `env/local.env.example` | Env template for local development. |
| `env/server.env.example` | Env template for server / production. |

All secrets are supplied at runtime via env files. No secrets are stored in compose files.

---

## Local Development (API + Postgres + pgAdmin)

Runs the API alongside a local Docker-managed Postgres and pgAdmin.

**1. Copy and fill the env file:**

```bash
cp env/local.env.example env/local.env
# Edit env/local.env and fill in META_APP_ID, META_APP_SECRET, GEMINI_API_KEY, etc.
```

**2. Start all services (from the `deploy/` directory):**

```bash
docker compose --env-file ./env/local.env -f docker-compose.yml -f docker-compose.local.db.yml up -d
```

**3. Access:**
- API:     http://localhost:5122
- pgAdmin: http://localhost:5050

**4. Stop:**

```bash
docker compose --env-file ./env/local.env -f docker-compose.yml -f docker-compose.local.db.yml down
```

---

## Server / Production (API only, external managed DB)

Postgres is **not** started in Docker. The API connects to your external managed database (Supabase, Neon, AWS RDS, etc.) via the connection string in the env file.

**1. Copy and fill the env file:**

```bash
cp env/server.env.example env/server.env
# Edit env/server.env:
#   - Set ConnectionStrings__DefaultConnection to your managed DB connection string.
#   - Set META_APP_ID, META_APP_SECRET, GEMINI_API_KEY, PUBLIC_URL, etc.
```

**2. Start the API (from the `deploy/` directory):**

```bash
docker compose --env-file ./env/server.env -f docker-compose.yml up -d
```

**3. Stop:**

```bash
docker compose --env-file ./env/server.env -f docker-compose.yml down
```

---

## Validate compose configs (dry-run)

Local:
```bash
docker compose --env-file ./env/local.env -f docker-compose.yml -f docker-compose.local.db.yml config
```

Server:
```bash
docker compose --env-file ./env/server.env -f docker-compose.yml config
```

---

## Notes

- `env/local.env` and `env/server.env` are git-ignored. Never commit real credentials.
- `ENV_FILE` inside the env file is the path Docker uses to pass variables into the container at runtime. It must match the env file you copied.
- The base `docker-compose.yml` expects a `Dockerfile` in `../backend/`. Build the image locally or adjust the `image:` tag before deploying to a server.
