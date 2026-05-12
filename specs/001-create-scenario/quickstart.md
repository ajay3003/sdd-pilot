# Quickstart: Scenario Management

**Branch**: `001-create-scenario` | **Date**: 2026-04-30

---

## Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| .NET SDK | 8.0+ | Backend API and Blazor WASM |
| Docker or Podman | any recent | PostgreSQL via Compose |
| `dotnet-ef` tool | 8.x | EF Core migrations |

Install the EF Core CLI tool once:
```bash
dotnet tool install --global dotnet-ef
```

> **Podman users**: `podman compose` is a drop-in replacement for `docker compose`. All
> commands below work unchanged with either runtime.

---

All commands below assume **`AIAssisted/`** as the working directory unless stated otherwise.

---

## 1 — Start the database

```bash
# from AIAssisted/
podman compose up -d postgres   # or: docker compose up -d postgres
```

`docker-compose.yml` exposes PostgreSQL on `localhost:5432` using the credentials in `.env`
(database `birknext`, user `birknext`, password `birknext`).

---

## 2 — Apply migrations

```bash
cd backend/BirkNext.Api
dotnet ef database update
```

---

## 3 — Run the backend

```bash
cd backend/BirkNext.Api
dotnet run
```

GraphQL endpoint: `http://localhost:5000/graphql`  
Banana Cake Pop IDE: `http://localhost:5000/graphql` (browser, Development only)

---

## 4 — Run the frontend

```bash
cd frontend/BirkNext.Web
dotnet run
```

Blazor WASM app: `http://localhost:5173`  
The Strawberry Shake client is pre-configured to target `http://localhost:5000/graphql`.

---

## 5 — Run all tests

```bash
# Backend (unit + integration + contract) — from AIAssisted/
cd backend
dotnet test

# Frontend Blazor component tests (bUnit) — from AIAssisted/
cd frontend
dotnet test
```

Integration tests spin up a real PostgreSQL container via Testcontainers — Docker or Podman must be running.

---

## Sample GraphQL operations

Use Banana Cake Pop or any GraphQL client pointed at `http://localhost:5000/graphql`.

### List scenarios
```graphql
query GetScenarios {
  scenarios(projectId: "proj-001") {
    id
    title
    description
    kind
    createdAt
  }
}
```

### Create a scenario
```graphql
mutation CreateScenario {
  createScenario(input: {
    title: "User can submit a valid scenario"
    description: "Happy path acceptance test"
    kind: TEST
    projectId: "proj-001"
  }) {
    scenario {
      id
      title
      kind
      createdAt
    }
    errors {
      code
      message
      field
    }
  }
}
```

---

## Environment variables

| Variable | Default | Description |
|----------|---------|-------------|
| `ConnectionStrings__Default` | (see `appsettings.json`) | PostgreSQL connection string |
| `FRONTEND_ORIGIN` | `http://localhost:5173` | Allowed CORS origin for `/graphql` |
| `Serilog__MinimumLevel` | `Information` | Log verbosity |
