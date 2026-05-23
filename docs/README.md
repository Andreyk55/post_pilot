# Post Pilot

A social media post management and scheduling tool.

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 18+](https://nodejs.org/)

## Quick Start

### 1. Bring up the full local stack (API + Worker + Postgres + pgAdmin + MinIO)

From `dev/`:

```powershell
docker compose --env-file ./local.env `
  -f docker-compose.yml `
  -f docker-compose.local.db.yml `
  -f docker-compose.local.storage.yml `
  -f docker-compose.local.depends.yml `
  up -d --build
```

cmd.exe equivalent:

```cmd
docker compose --env-file ./local.env ^
  -f docker-compose.yml ^
  -f docker-compose.local.db.yml ^
  -f docker-compose.local.storage.yml ^
  -f docker-compose.local.depends.yml ^
  up -d --build
```

Or just use the wrapper script: `pwsh -File dev/scripts/start.ps1`

Long-running containers: **api** (5122), **publisher** (Worker, no port), **postgres** (5432), **pgadmin** (5050), **minio** (9000 API / 9001 console).
Setup container that creates the `postpilot-media` bucket and exits: **minio-init**.

Migrations run automatically on API startup.

### 2. Start the frontend (stays outside Docker)

```bash
cd frontend
npm install   # first time only
npm run dev
```

The frontend talks to the API at `http://localhost:5122` and uploads media directly to MinIO at `http://localhost:9000` via presigned PUT URLs.

## Access Points

| Service | URL | Credentials |
|---------|-----|-------------|
| Frontend | http://localhost:5173 | - |
| Backend API | http://localhost:5122 | - |
| Swagger UI | http://localhost:5122/swagger | - |
| pgAdmin | http://localhost:5050 | admin@postpilot.com / admin |
| MinIO Console | http://localhost:9001 | postpilot / postpilot-password |
| MinIO Bucket | `postpilot-media` | - |

## Local Object Storage (MinIO)

Media uploads go directly from the browser to MinIO via S3-compatible presigned PUT URLs:

1. Browser calls `POST /api/media/uploads/init` with `{ fileName, contentType, sizeBytes }`.
2. API creates a `Media` row (status `PendingUpload`), returns a presigned `uploadUrl` (host `localhost:9000`), a `storageKey`, and a `mediaId`.
3. Browser `PUT`s the bytes directly to the `uploadUrl`.
4. Browser calls `POST /api/media/uploads/complete` with `{ mediaId }`; the API verifies the object via a HEAD request and flips the row to `Uploaded`.

Why the URL uses `localhost:9000` (public) while the API talks to `minio:9000` (internal): the browser cannot resolve Docker DNS, but the S3 signature is bound to the endpoint it was signed against — so the provider keeps two `AmazonS3Client` instances and uses the public one only for presigning.

For real Meta publishing from a local dev environment, set `App__PublicUrl` to a public tunnel URL (ngrok/cloudflared) pointing at the API on `5122`. Meta will fetch media via `{App.PublicUrl}/api/media/files/{storageKey}`, which the API streams from MinIO.

Production storage provider is intentionally not implemented yet — the `IMediaStorageProvider` abstraction is in place for any S3-compatible backend (S3, R2, Spaces, B2, Wasabi, Hetzner) once a production provider is chosen.

## Database Connection (pgAdmin)

When connecting pgAdmin to PostgreSQL:

1. Login to pgAdmin at http://localhost:5050
2. Right-click "Servers" → "Register" → "Server..."
3. **General tab:** Name = `PostPilot`
4. **Connection tab:**
   - Host: `postgres`
   - Port: `5432`
   - Database: `postpilot`
   - Username: `postgres`
   - Password: `postgres`
5. Click "Save"

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/posts` | List all scheduled posts |
| GET | `/api/posts/{id}` | Get a single post |
| POST | `/api/posts` | Create a scheduled post |
| PUT | `/api/posts/{id}` | Update a post |
| DELETE | `/api/posts/{id}` | Delete a post |

## Project Structure

```
post_pilot/
├── backend/                 # .NET 10 Web API + Worker (publisher) projects
│   ├── Controllers/         # API endpoints
│   ├── Data/                # DbContext
│   ├── Entities/            # Database models (includes Media side-table)
│   ├── Enums/               # Platform, PostStatus, MediaUploadStatus
│   ├── Services/Media/      # IMediaStorageProvider, S3CompatibleMediaStorageProvider, etc.
│   ├── Migrations/          # EF Core migrations
│   └── publisher/           # PostPilot.Publisher worker project
├── frontend/                # React + TypeScript + Vite
│   └── src/api/media.ts     # init/complete upload flow
├── build/
│   └── Dockerfile                          # multi-stage: api + publisher targets (shared by dev + CI prod)
├── dev/                                    # local development stack
│   ├── docker-compose.yml                  # api + publisher (builds from source)
│   ├── docker-compose.local.db.yml         # postgres + pgadmin
│   ├── docker-compose.local.storage.yml    # minio + minio-init
│   ├── docker-compose.local.depends.yml    # api/publisher depends_on overrides
│   ├── local.env                           # local env file (MediaStorage__*, etc.) — gitignored
│   └── scripts/                            # start.ps1, stop.ps1, restart.ps1, reset-db.ps1, pgadmin-*.ps1
├── prod/                                   # VPS production stack
│   ├── docker-compose.yml                  # pulls images from GHCR
│   ├── server.env.example                  # template for /opt/postpilot/server.env
│   ├── nginx/postpilot-api.conf            # host nginx config template
│   └── scripts/                            # deploy.sh, check-prod.sh
└── docs/
    └── README.md
```

## Stopping Services

```powershell
# Stop everything
docker compose --env-file ./local.env `
  -f docker-compose.yml `
  -f docker-compose.local.db.yml `
  -f docker-compose.local.storage.yml `
  -f docker-compose.local.depends.yml `
  down

# Stop with data cleanup (removes Postgres + MinIO data)
docker compose --env-file ./local.env `
  -f docker-compose.yml `
  -f docker-compose.local.db.yml `
  -f docker-compose.local.storage.yml `
  -f docker-compose.local.depends.yml `
  down -v
```

## Environment Configuration

- **Development:** Uses `appsettings.Development.json` → local PostgreSQL
- **Production:** Set `ConnectionStrings__DefaultConnection` environment variable → AWS RDS

## ngrok Setup (Local Development with Meta Webhooks)

ngrok exposes your local backend to the internet, required for Meta/Facebook OAuth callbacks during development.

### 1. Install ngrok

Download from [ngrok.com](https://ngrok.com/download) or:

```bash
# Windows (choco)
choco install ngrok

# macOS
brew install ngrok
```

### 2. Authenticate ngrok

```bash
ngrok config add-authtoken YOUR_AUTH_TOKEN
```

Get your auth token from [ngrok dashboard](https://dashboard.ngrok.com/get-started/your-authtoken).

### 3. Start ngrok

```bash
ngrok http 5122
```

This will give you a public URL like `https://abc123.ngrok-free.app`.

### 4. Configure Meta App

In your [Meta Developer Console](https://developers.facebook.com/apps/):

1. Go to your app → **Facebook Login** → **Settings**
2. Add the ngrok URL to **Valid OAuth Redirect URIs**:
   - `https://abc123.ngrok-free.app/api/auth/callback`
3. Go to **Settings** → **Basic** and add to **App Domains**:
   - `abc123.ngrok-free.app`

### 5. Update Frontend API URL

Update your frontend to use the ngrok URL for API calls during testing, or use the ngrok URL directly in the browser.

**Note:** The ngrok URL changes each time you restart (unless you have a paid plan with reserved domains).

## Tech Stack

- **Backend:** .NET 10, Entity Framework Core, PostgreSQL
- **Frontend:** React 19, TypeScript, Vite
- **Database:** PostgreSQL 16
- **Tools:** Swagger, pgAdmin
