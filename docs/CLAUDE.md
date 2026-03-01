# Post Pilot

A social media post management and scheduling tool.

## Project Overview

Post Pilot allows users to schedule and manage posts across social media platforms.

## Features

### MVP - Post Scheduling
- Schedule posts (text, images, videos) for a specific date and time
- Example: Schedule a post for tomorrow at 10 AM

### Future Features
- [ ] Delete confirmation dialog - Ask user "Are you sure?" before deleting a post

## Tech Stack

- **Backend:** .NET 10 (C#) ASP.NET Core Web API (long-running Kestrel server)
- **Frontend:** React + TypeScript + Vite
- **Database:** PostgreSQL
- **Media Storage:** Local filesystem (APP_RUN_MODE=local) or generic storage provider (APP_RUN_MODE=server, stub — implement IMediaStorageProvider)
- **Post Scheduling:** In-process BackgroundService (`PostPublishingWorker`) polling every 30s

## Supported Platforms

- Facebook Pages (Meta Graph API)
- Instagram Business Accounts

## Architecture Decisions

- **Monorepo structure:**
  - `/backend` - .NET 10 Web API (PostPilot.Api)
  - `/frontend` - React + TypeScript app (Vite)
- **Single long-running server:** No Lambda/SQS/EventBridge. One ASP.NET Core app serves HTTP APIs and runs the publishing worker as a hosted BackgroundService.

## How to Run

- **Backend:** `cd backend && dotnet run`
- **Frontend:** `cd frontend && npm run dev`

## Pending Tasks

- [x] Add .gitignore for backend (obj/, bin/ folders)
- [x] Set up database (PostgreSQL + EF Core)
- [x] Create post scheduling API endpoints
- [x] Connect frontend to backend API
- [x] Set up media storage (local filesystem)
- [x] Integrate Meta Graph API

## Notes

- Project started: January 2026
- Using TypeScript (.tsx files) for type safety in React

# Architecture

## Application technologies

- Backend: C# .NET 10, ASP.NET Core (single long-running server)
- Frontend: React (static SPA)
- Database: PostgreSQL
- API docs: Swagger/OpenAPI (enabled in non-prod / protected in prod)
- Social platform v1: Facebook Pages + Instagram (Meta Graph API)
- Content v1: text + images + videos

## Scheduling + publishing pipeline

`PostPublishingWorker` (BackgroundService, polls every 30 seconds):
- Finds due posts in DB (Scheduled + ScheduledAt <= now, or RetryPending/Processing + NextRetryAt <= now)
- Atomically claims them (status → Publishing)
- Publishes to Meta via platform-specific publishers
- Updates DB status (Published / Failed) + stores external IDs + errors
- Stuck recovery: posts in Publishing status for >5 minutes are recovered with retry or marked Failed

## Media handling

- Local filesystem (APP_RUN_MODE=local) or generic storage provider (APP_RUN_MODE=server)
- UI uploads files via pre-signed URLs (local: backend PUT endpoint; server: storage provider pre-signed URL)
- Publishing: generates a download URL and passes it to Meta

## Auth (v1)

- App-level JWT auth (simple email/password or later federated login)
- Store users in Postgres

## Secrets & configuration

- Environment variables for sensitive values (DB connection string, Meta app secret, etc.)
