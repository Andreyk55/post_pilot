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