# QA Review Studio Troubleshooting Guide

## Frontend Does Not Start

Try:

```powershell
.\scripts\start-local.ps1 -FrontendOnly -SkipContainers
```

If the frontend port is busy:

```text
Port 5173 is already in use
```

Inspect the owning process:

```powershell
netstat -ano | findstr :5173
```

Stop a known stale process:

```powershell
taskkill /PID <pid> /F
```

If the owner is a stale local QA Review Studio/BirkNext `dotnet` process, rerun:

```powershell
.\scripts\start-local.ps1 -ForceKillPorts
```

## Backend Does Not Start

Check:

- database container is running
- connection string is correct
- backend builds
- ports are free

Try:

```powershell
.\scripts\start-local.ps1 -BackendOnly
```

## PostgreSQL Password Authentication Failed

Example:

```text
Npgsql.PostgresException 28P01: password authentication failed for user "birknext"
```

Local compose credentials are usually defined in:

```text
AIAssisted/.env
```

If authentication still fails, the most common cause is an existing `postgres_data` volume initialized with older credentials.

PostgreSQL does not update stored database passwords when `.env` changes.

Options:

- restore `.env` to credentials used when the volume was created
- set `ConnectionStrings__Default` to match the existing local database
- recreate the local PostgreSQL volume if local data can be discarded

## PowerShell Script Is Blocked

Use:

```text
scripts/start-local.bat
```

or:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

## Podman Machine Problems

Check:

```bash
podman machine list
```

Start:

```bash
podman machine start
```

Only if no machine exists:

```bash
podman machine init
podman machine start
```

If machine is running but Podman is not responding:

```bash
podman machine stop
podman machine start
podman info
```

## GraphQL Enum Does Not Support USER_STORY

This is expected unless the backend has explicitly added USER_STORY as a first-class artifact type.

Recommended MVP model:

```text
Artifact types:
- REQUIREMENT
- TEST
- NEEDS_CLARIFICATION

User Story:
- ContextHeading / grouping metadata
```

Do not add USER_STORY to GraphQL unless the product intentionally decides to persist user stories as first-class artifacts.

## Review State Not Saved

Check:

- backend is running
- database is running
- GraphQL save-review mutation succeeds
- browser console has no network errors

## Analyze Results Disappear After Navigation

Expected desired behavior:

- active extraction session should restore when returning to Specification Review
- filters, review decisions, and expanded groups should be preserved if session persistence is implemented

If not:

- check session/local storage implementation
- check browser console
- verify restore logic on `/extract`

## Saved Review Does Not Create Test Scenarios

This can be expected depending on workflow.

Save Review persists reviewed QA artifacts.

Saving selected TEST artifacts creates test scenarios where supported.

## UI Looks Old or Broken

Check:

- latest frontend is running
- browser cache is refreshed
- shared CSS files are included
- CSS isolation files are loaded
