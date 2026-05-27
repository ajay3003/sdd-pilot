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
