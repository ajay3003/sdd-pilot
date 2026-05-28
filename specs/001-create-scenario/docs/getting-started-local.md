# BirkNext Local Startup Guide

## Recommended Startup

Use:

```text
scripts/start-local.bat
```

or:

```powershell
.\scripts\start-local.ps1
```

## What the Startup Script Does

1. Starts container services using Podman by default
2. Checks Podman machine readiness
3. Starts/restarts Podman machine where possible
4. Loads PostgreSQL settings from `AIAssisted/.env`
5. Exports a matching `ConnectionStrings__Default` value for the backend
6. Waits for database/container initialization
7. Starts backend in a separate PowerShell window
8. Waits before starting frontend
9. Starts frontend in a separate PowerShell window

## Local PostgreSQL Credentials

The local compose file and backend launcher use:

```text
AIAssisted/.env
```

Default values:

```text
POSTGRES_DB=birknext
POSTGRES_USER=birknext
POSTGRES_PASSWORD=birknext
```

PostgreSQL stores the initial username and password in the `postgres_data`
volume. If you change these values after the volume already exists, recreate
the local database volume or set `ConnectionStrings__Default` to match the
existing database.

## Runtime Options

Podman is default:

```powershell
.\scripts\start-local.ps1
```

Docker:

```powershell
.\scripts\start-local.ps1 -ContainerRuntime docker
```

Frontend only:

```powershell
.\scripts\start-local.ps1 -FrontendOnly -SkipContainers
```

Clean stale local frontend/backend processes before starting:

```powershell
.\scripts\start-local.ps1 -ForceKillPorts
```

Longer delays:

```powershell
.\scripts\start-local.ps1 -ContainerDelaySeconds 20 -BackendDelaySeconds 30
```

## Busy Frontend or Backend Ports

The launcher checks the configured backend and frontend ports before starting
the apps. By default it only warns when a port is already in use and does not
kill anything automatically.

Example:

```text
Port 5173 is already in use
```

Inspect the port manually:

```powershell
netstat -ano | findstr :5173
```

Stop a known stale process:

```powershell
taskkill /PID <pid> /F
```

For stale local BirkNext `dotnet` processes, the launcher can clean them up:

```powershell
.\scripts\start-local.ps1 -ForceKillPorts
```

## Prerequisites

- .NET SDK 8
- Podman Desktop or Docker Desktop
- Git

## Useful Paths

```text
/extract
/scenarios
/dashboard
```

## Verify Extraction and Review

1. Open `/extract`
2. Import or paste text
3. Click **Extract Scenarios**
4. Change review statuses
5. Click **Save Review**
6. Select candidates
7. Click **Save Selected**
8. Open `/scenarios`

## Podman Manual Commands

```bash
podman machine list
podman machine start
podman ps
```

Only if no machine exists:

```bash
podman machine init
podman machine start
```

## Run Tests

Backend:

```bash
cd AIAssisted/backend
dotnet test
```

Frontend:

```bash
cd AIAssisted/frontend
dotnet test
```
