# BirkNext Troubleshooting Guide

## Frontend Does Not Start

Check:

- .NET SDK 8 is installed
- correct frontend project is selected
- startup script found the correct `.csproj`
- backend is not required for static startup but is required for GraphQL features

Try:

```powershell
.\scripts\start-local.ps1 -FrontendOnly -SkipContainers
```

## Backend Does Not Start

Check:

- database container is running
- connection string is correct
- backend project builds
- ports are not already in use

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

## Database Not Running

For Podman:

```bash
podman ps
podman compose up -d
```

For Docker:

```bash
docker ps
docker compose up -d
```

## Frontend Cannot Reach Backend

Check:

- backend window is still running
- backend URL is correct
- GraphQL endpoint is available
- browser console does not show network errors

## GraphQL Mutation Service Missing

Example error:

```text
There is no registered service of type ICreateScenariosMutation
```

Cause:

- generated Strawberry Shake operation exists
- DI registration is missing

Fix:

- register the generated mutation operation in frontend `Program.cs`
- rebuild frontend
- refresh browser

## No Scenarios Extracted

Possible causes:

- input contains mostly prose
- no recognizable patterns
- ignore prefixes suppress extraction
- configuration changed extraction behavior

Try:

- bullet lists
- clearer requirement statements
- explicit `Verify...`
- explicit `Clarify...`

## Unexpected Classification

Check:

- configured prefixes
- configured keywords
- enabled rule groups
- rule priority overrides

Remember:

```text
same input + same configuration = same output
```

## File Import Fails

Check:

- file extension is `.md` or `.txt`
- file is not empty
- file is not too large
- file is readable text
- file is not binary

## Configuration Not Applied

Possible causes:

- application was not restarted
- configuration is invalid
- fallback to defaults occurred

Fix:

1. correct configuration
2. restart frontend
3. verify extraction behavior again

## UI Looks Old or Broken

Check:

- latest frontend files are running
- browser cache is refreshed
- shared CSS files are included
- old Blazor template pages are removed
