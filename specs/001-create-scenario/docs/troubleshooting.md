# BirkNext Troubleshooting Guide

## Frontend Does Not Start

Try:

```powershell
.\scripts\start-local.ps1 -FrontendOnly -SkipContainers
```

Check:

- .NET SDK 8 is installed
- correct frontend project is selected
- browser console errors

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

If the owner is a stale local BirkNext `dotnet` process, rerun:

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

If the backend port is busy:

```text
Port 5000 is already in use
```

Inspect the owning process:

```powershell
netstat -ano | findstr :5000
```

Stop a known stale process:

```powershell
taskkill /PID <pid> /F
```

For stale local BirkNext `dotnet` processes:

```powershell
.\scripts\start-local.ps1 -ForceKillPorts
```

## PostgreSQL Password Authentication Failed

Example:

```text
Npgsql.PostgresException 28P01: password authentication failed for user "birknext"
```

Local compose credentials are defined in:

```text
AIAssisted/.env
```

The launcher reads that file and exports a matching backend
`ConnectionStrings__Default` value. If authentication still fails, the most
common cause is an existing `postgres_data` volume that was initialized with
older credentials. PostgreSQL does not update the stored database password when
`.env` changes.

Check what is listening on port 5432:

```powershell
netstat -ano | Select-String ':5432'
```

Fix options:

- Restore `AIAssisted/.env` to the credentials used when the volume was created.
- Set `ConnectionStrings__Default` to match the existing local database.
- Recreate the local PostgreSQL volume if local data can be discarded.

For Podman, inspect volumes first:

```bash
podman volume ls
```

Then remove only the BirkNext local PostgreSQL volume when you intentionally
want a fresh database.

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

Restart Podman Desktop if needed.

## GraphQL Mutation Service Missing

Example:

```text
There is no registered service of type ICreateScenariosMutation
```

Fix:

- register generated Strawberry Shake operation in frontend `Program.cs`
- rebuild frontend
- refresh browser

## File Import Fails

Check:

- file extension is `.md` or `.txt`
- file is not empty
- file is not too large
- file is readable text
- file is not binary

## Review State Not Saved

Check:

- backend is running
- database is running
- GraphQL save-review mutation succeeds
- browser console has no network errors

Remember:

```text
Save Review persists reviewed candidates.
Save Selected creates finalized scenarios.
```

## Saved Review Does Not Create Scenarios

This is expected. Use **Save Selected** to create finalized scenarios.

## UI Looks Old or Broken

Check:

- latest frontend is running
- browser cache is refreshed
- shared CSS files are included
