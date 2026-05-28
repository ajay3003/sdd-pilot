# QA Review Studio Local Startup Guide

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
5. Exports a matching `ConnectionStrings__Default` value for the backend where supported
6. Waits for database/container initialization
7. Starts backend in a separate PowerShell window
8. Waits before starting frontend
9. Starts frontend in a separate PowerShell window

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

## Useful Paths

```text
/dashboard
/extract
/scenarios
/scenarios/new
/compare
```

UI labels may show:

```text
Dashboard
Specification Review
QA Artifact Library
New Test Scenario
Compare Specs
```

## Verify Specification Review

1. Open `/extract`
2. Import or paste a Speckit `spec.md`
3. Confirm Speckit Structured Spec is selected if that is the default
4. Click **Analyze Specification**
5. Review grouped QA artifacts
6. Change review statuses
7. Click **Save Review**
8. Save selected TEST artifacts where supported
9. Open QA Artifact Library

## Verify QA Artifact Library

1. Open `/scenarios`
2. Confirm TEST filter is selected by default if implemented
3. Confirm artifacts are grouped or filterable by type
4. Confirm requirements, tests, and clarification items are distinct

## Verify New Test Scenario

1. Open `/scenarios/new`
2. Confirm page title is **New Test Scenario**
3. Confirm there is no artifact type selector
4. Create a manual test scenario
5. Confirm it appears as TEST in QA Artifact Library

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
