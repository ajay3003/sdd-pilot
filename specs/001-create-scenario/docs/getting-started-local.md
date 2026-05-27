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
4. Waits for database/container initialization
5. Starts backend in a separate PowerShell window
6. Waits before starting frontend
7. Starts frontend in a separate PowerShell window

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

Longer delays:

```powershell
.\scripts\start-local.ps1 -ContainerDelaySeconds 20 -BackendDelaySeconds 30
```

## Prerequisites

- .NET SDK 8
- Podman Desktop or Docker Desktop
- Git

## Useful Paths

```text
/extract
/scenarios
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
