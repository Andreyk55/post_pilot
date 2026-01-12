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

- **Backend:** .NET 10 (C#) Web API
- **Frontend:** React + TypeScript + Vite
- **Database:** (To be decided - PostgreSQL suggested)
- **Job Scheduling:** (To be decided - Hangfire suggested)
- **Cache/Queue:** (To be decided - Redis suggested)

## Supported Platforms

- (To be decided - e.g., Twitter/X, Instagram, Facebook, LinkedIn, etc.)

## Architecture Decisions

- **Monorepo structure:**
  - `/backend` - .NET 10 Web API (PostPilot.Api)
  - `/frontend` - React + TypeScript app (Vite)



## How to Run

- **Backend:** `cd backend && dotnet run`
- **Frontend:** `cd frontend && npm run dev`

## Pending Tasks

- [x] Add .gitignore for backend (obj/, bin/ folders)
- [x] Set up database (PostgreSQL + EF Core)
- [x] Create post scheduling API endpoints
- [x] Connect frontend to backend API

## Notes

- Project started: January 2026
- Using TypeScript (.tsx files) for type safety in React



# infra -------------------------

Application technologies

Backend: C# .NET 10, ASP.NET Core (single API app)

Frontend: React (static SPA)

Database: PostgreSQL

API docs: Swagger/OpenAPI (enabled in non-prod / protected in prod)

Social platform v1: Facebook Pages (Meta Graph API)

Content v1: text + images (videos supported later, same architecture)

AWS infrastructure (Lambda architecture)
Frontend hosting

S3: hosts the React build (static files)

CloudFront: CDN + HTTPS + custom domain

(Optional) Route 53 for DNS

API layer

API Gateway (HTTP API) → API Lambda

Single Lambda handles all UI routes (/posts, /analytics, /settings, etc.)

Routing handled by ASP.NET Core controllers/minimal APIs inside the Lambda

Auth (v1)

App-level JWT auth (simple email/password or later federated login)

Store users in Postgres

(Later option) move to Cognito if you want managed auth

Database

RDS PostgreSQL

(Recommended soon) RDS Proxy to protect connections as traffic grows

Scheduling + publishing pipeline

EventBridge Scheduler (every 1 minute) → Dispatcher Lambda

Finds due posts in DB (Pending + scheduled_at <= now)

Atomically claims them (Publishing)

Sends each post as a message to SQS

SQS queue → Publisher Lambda

1 post = 1 message = 1 Lambda execution (batch size = 1 initially)

Publishes to Meta

Updates DB status (Published / Failed) + stores external IDs + errors

Retries handled by SQS; failures go to DLQ

DLQ (Dead Letter Queue)

Stores messages that repeatedly fail

Enables manual retry + debugging

Media handling (images now; videos later)

S3 media bucket

UI uploads files directly to S3 using pre-signed URLs

Publishing:

Images: Publisher Lambda generates pre-signed GET URL and passes it to Meta (Meta pulls the image)

Videos (later): same pattern if supported; otherwise Publisher Lambda streams S3 → Meta with resumable upload + processing state

Secrets & configuration

SSM Parameter Store (standard) for non-sensitive config

Secrets Manager for sensitive values (DB password, Meta app secret, JWT secret)
