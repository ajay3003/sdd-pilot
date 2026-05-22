# BirkNext Local Startup Guide

## Purpose

This guide explains different ways to start BirkNext locally for:

- development
- testing
- demos
- troubleshooting
- SDD workflow verification

---

# Supported Local Setup Variants

BirkNext can typically be started using:

| Option | Recommended | Notes |
|---|---|---|
| Podman | YES | Preferred container runtime |
| Docker Desktop | YES | Common Windows setup |
| Visual Studio | YES | Simplest debugging experience |
| VS Code + terminal | YES | Lightweight workflow |
| CLI only | YES | Fastest advanced workflow |

---

# Prerequisites

Install:

- .NET SDK 8
- Podman or Docker
- Git

Optional:

- Visual Studio 2022
- VS Code

---

# Typical Repository Structure

```text
BirkNext/
├── AIAssisted/
│   ├── frontend/
│   └── backend/
├── specs/
├── CLAUDE.md
└── .gitignore
```

---

# Option 1 — Podman (Recommended)

## Start PostgreSQL

From repository root:

```bash
podman compose up -d
```

Alternative:

```bash
podman-compose up -d
```

Verify containers:

```bash
podman ps
```

View logs:

```bash
podman logs <container-name>
```

Stop containers:

```bash
podman compose down
```

---

# Start Backend

Open terminal:

```bash
cd AIAssisted/backend
dotnet restore
dotnet build
dotnet run
```

---

# Start Frontend

Open second terminal:

```bash
cd AIAssisted/frontend
dotnet restore
dotnet build
dotnet run
```

---

# Option 2 — Docker Desktop

## Start Database

```bash
docker compose up -d
```

Verify:

```bash
docker ps
```

Logs:

```bash
docker logs <container-name>
```

Stop:

```bash
docker compose down
```

---

# Start Backend

```bash
cd AIAssisted/backend
dotnet restore
dotnet build
dotnet run
```

---

# Start Frontend

```bash
cd AIAssisted/frontend
dotnet restore
dotnet build
dotnet run
```

---

# Option 3 — Visual Studio

## Backend

Open:

```text
AIAssisted/backend/BirkNext.sln
```

Press:

```text
F5
```

or:

```text
Ctrl + F5
```

---

## Frontend

Open:

```text
AIAssisted/frontend/BirkNext.sln
```

Press:

```text
F5
```

---

# Option 4 — VS Code

Open repository:

```bash
code .
```

Use integrated terminals.

Backend terminal:

```bash
cd AIAssisted/backend
dotnet run
```

Frontend terminal:

```bash
cd AIAssisted/frontend
dotnet run
```

---

# Option 5 — CLI Only Workflow

## Backend

```bash
cd AIAssisted/backend
dotnet restore
dotnet build
dotnet run
```

---

## Frontend

```bash
cd AIAssisted/frontend
dotnet restore
dotnet build
dotnet run
```

---

# Verify Backend

GraphQL should normally be available at:

```text
/graphql
```

Example:

```text
https://localhost:xxxx/graphql
```

---

# Verify Frontend

Frontend should open in browser automatically.

If not, open URL from terminal output manually.

Example:

```text
https://localhost:xxxx
```

---

# Verify Scenario Extraction

Open:

```text
/extract
```

Paste:

```text
- User can archive scenarios
- Verify archive button visibility
- Clarify archive retention policy
```

Click:

```text
Extract
```

Expected:

- REQUIREMENT candidate
- TEST candidate
- NEEDS_CLARIFICATION candidate

---

# Run Tests

## Frontend Tests

```bash
cd AIAssisted/frontend
dotnet test BirkNext.sln
```

---

## Backend Tests

```bash
cd AIAssisted/backend
dotnet test BirkNext.sln
```

---

# Useful Development Commands

## Clean Build

```bash
dotnet clean
dotnet build
```

---

## Verify Formatting

```bash
dotnet format --verify-no-changes
```

---

## Restore Dependencies

```bash
dotnet restore
```

---

# Common Problems

## Database Not Running

Verify:

```bash
podman ps
```

or:

```bash
docker ps
```

Restart database containers if needed.

---

## Frontend Cannot Reach Backend

Check:

- backend running
- correct ports
- HTTPS certificates
- CORS configuration

---

## GraphQL Errors

Verify:

- backend started successfully
- schema generated correctly
- Strawberry Shake client regenerated if schema changed

---

## Configuration Changes Not Applied

Restart frontend application.

Extraction configuration is loaded at startup.

---

# Useful Local URLs

Adjust ports based on terminal output.

```text
Frontend:  https://localhost:<frontend-port>
Backend:   https://localhost:<backend-port>
GraphQL:   https://localhost:<backend-port>/graphql
Extract:   /extract
Scenarios: /scenarios
```

---

# Recommended Workflow

Recommended daily workflow:

1. Start Podman
2. Start PostgreSQL container
3. Start backend
4. Start frontend
5. Verify extraction
6. Run tests before commit

---

# Notes

- Exact ports depend on launch settings
- Podman is fully supported
- Docker and Podman commands are intentionally both documented
