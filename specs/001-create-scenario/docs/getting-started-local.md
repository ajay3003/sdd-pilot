# BirkNext Local Startup Guide

## Purpose

This guide explains how to start BirkNext locally for development, testing, demos, and troubleshooting.

## Recommended Startup Method

Use the local startup scripts:

```text
scripts/start-local.bat
scripts/start-local.ps1
```

Recommended for Windows users:

```text
Double-click scripts/start-local.bat
```

The batch file runs the PowerShell script with execution-policy bypass.

## What the Startup Script Does

The script:

1. Starts container services using Podman by default
2. Waits for containers/database to initialize
3. Starts backend in a separate PowerShell window
4. Waits before starting frontend
5. Starts frontend in a separate PowerShell window
6. Prints guidance for accessing frontend URLs

## Default Runtime

Podman is the default container runtime.

```powershell
.\scripts\start-local.ps1
```

Docker can be used instead:

```powershell
.\scripts\start-local.ps1 -ContainerRuntime docker
```

## Common Script Options

### Default startup

```powershell
.\scripts\start-local.ps1
```

### Fast mode

Use only after successful build:

```powershell
.\scripts\start-local.ps1 -Fast
```

### Skip containers

```powershell
.\scripts\start-local.ps1 -SkipContainers
```

### Longer delays

```powershell
.\scripts\start-local.ps1 -ContainerDelaySeconds 20 -BackendDelaySeconds 30
```

### Backend only

```powershell
.\scripts\start-local.ps1 -BackendOnly
```

### Frontend only

```powershell
.\scripts\start-local.ps1 -FrontendOnly -SkipContainers
```

## Prerequisites

Install:

- .NET SDK 8
- Podman Desktop or Docker Desktop
- Git

Optional:

- Visual Studio 2022
- VS Code

## Typical Repository Structure

```text
BirkNext/
├── AIAssisted/
│   ├── frontend/
│   └── backend/
├── scripts/
│   ├── start-local.bat
│   └── start-local.ps1
├── specs/
├── CLAUDE.md
└── .gitignore
```

## Manual Startup with Podman

```bash
podman compose up -d
podman ps
```

Stop:

```bash
podman compose down
```

## Start Backend Manually

```bash
cd AIAssisted/backend
dotnet restore
dotnet build
dotnet run
```

If needed:

```bash
dotnet run --project path/to/backend-project.csproj
```

## Start Frontend Manually

```bash
cd AIAssisted/frontend
dotnet restore
dotnet build
dotnet run
```

If needed:

```bash
dotnet run --project path/to/frontend-project.csproj
```

## Access the Frontend

Look in the frontend PowerShell window for:

```text
Now listening on: https://localhost:xxxx
```

Open that URL in the browser.

Useful paths:

```text
/extract
/scenarios
```

## Verify Scenario Extraction

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

Expected:

- REQUIREMENT candidate
- TEST candidate
- NEEDS_CLARIFICATION candidate

## Verify File Import

On the Extract page:

1. Import a `.md` file
2. Verify text appears in the input area
3. Click Extract
4. Review candidates
5. Save selected candidates

Repeat with a `.txt` file.

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

## Useful Development Commands

```bash
dotnet restore
dotnet build
dotnet test
dotnet format --verify-no-changes
```

## Common Problems

### PowerShell blocks script execution

Run through the batch file:

```text
scripts/start-local.bat
```

Or run:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

### Database not running

```bash
podman ps
```

or:

```bash
docker ps
```

### Frontend URL unknown

Check the frontend terminal for:

```text
Now listening on: https://localhost:xxxx
```
