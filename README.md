# Post Pilot

A social media post management and scheduling tool.

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 18+](https://nodejs.org/)

## Quick Start

### 1. Start Database (PostgreSQL + pgAdmin)

```bash
docker-compose up -d
```

This starts:
- **PostgreSQL** on port 5432
- **pgAdmin** on port 5050

### 2. Run Database Migrations

```bash
cd backend
dotnet ef database update
```

### 3. Start Backend API

```bash
cd backend
dotnet run
```

### 4. Start Frontend

```bash
cd frontend
npm install   # first time only
npm run dev
```

## Access Points

| Service | URL | Credentials |
|---------|-----|-------------|
| Frontend | http://localhost:5173 | - |
| Backend API | http://localhost:5122 | - |
| Swagger UI | http://localhost:5122/swagger | - |
| pgAdmin | http://localhost:5050 | admin@postpilot.com / admin |

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
├── backend/                 # .NET 10 Web API
│   ├── Controllers/         # API endpoints
│   ├── Data/                # DbContext
│   ├── Entities/            # Database models
│   ├── Enums/               # Platform, PostStatus
│   └── Migrations/          # EF Core migrations
├── frontend/                # React + TypeScript + Vite
│   ├── src/
│   │   ├── api/             # API client
│   │   ├── components/      # React components
│   │   └── pages/           # Page components
│   └── package.json
├── docker-compose.yml       # PostgreSQL + pgAdmin
└── README.md
```

## Stopping Services

```bash
# Stop database containers
docker-compose down

# Stop with data cleanup (removes database data)
docker-compose down -v
```

## Environment Configuration

- **Development:** Uses `appsettings.Development.json` → local PostgreSQL
- **Production:** Set `ConnectionStrings__DefaultConnection` environment variable → AWS RDS

## Tech Stack

- **Backend:** .NET 10, Entity Framework Core, PostgreSQL
- **Frontend:** React 19, TypeScript, Vite
- **Database:** PostgreSQL 16
- **Tools:** Swagger, pgAdmin
