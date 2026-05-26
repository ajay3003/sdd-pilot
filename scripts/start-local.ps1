param(
    [ValidateSet("podman", "docker")]
    [string]$ContainerRuntime = "podman",

    [switch]$Fast,
    [switch]$SkipContainers,
    [switch]$BackendOnly,
    [switch]$FrontendOnly,

    [int]$ContainerDelaySeconds = 10,
    [int]$BackendDelaySeconds = 20
)

$ErrorActionPreference = "Stop"

function Info($message) { Write-Host "[INFO] $message" -ForegroundColor Cyan }
function Ok($message) { Write-Host "[OK] $message" -ForegroundColor Green }
function Warn($message) { Write-Host "[WARN] $message" -ForegroundColor Yellow }
function Fail($message) { Write-Host "[ERROR] $message" -ForegroundColor Red }

function Command-Exists($command) {
    return $null -ne (Get-Command $command -ErrorAction SilentlyContinue)
}

function Require-Command($command, $hint) {
    if (-not (Command-Exists $command)) {
        Fail "$command was not found."
        Write-Host $hint -ForegroundColor Yellow
        throw "Missing command: $command"
    }
    Ok "$command found"
}

function Start-ContainerServices {
    if ($SkipContainers) {
        Warn "Skipping container startup."
        return
    }

    Info "Starting container services using $ContainerRuntime..."

    if ($ContainerRuntime -eq "podman") {
        Require-Command "podman" "Install Podman Desktop or add podman to PATH."

        if (Command-Exists "podman-compose") {
            podman-compose up -d
        }
        else {
            podman compose up -d
        }

        podman ps
    }
    else {
        Require-Command "docker" "Install Docker Desktop or add docker to PATH."
        docker compose up -d
        docker ps
    }

    if ($ContainerDelaySeconds -gt 0) {
        Info "Waiting $ContainerDelaySeconds seconds for containers/database to initialize..."
        Start-Sleep -Seconds $ContainerDelaySeconds
    }
}

function Find-ProjectFile {
    param(
        [string]$BasePath,
        [string]$PreferredName
    )

    $preferred = Get-ChildItem -Path $BasePath -Recurse -Filter "$PreferredName.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($preferred) { return $preferred.FullName }

    $allProjects = Get-ChildItem -Path $BasePath -Recurse -Filter "*.csproj" -ErrorAction SilentlyContinue
    $nonTest = $allProjects | Where-Object { $_.FullName -notmatch "\.Tests\.csproj$" } | Select-Object -First 1

    if ($nonTest) { return $nonTest.FullName }

    throw "No runnable project file found under $BasePath"
}

function Start-DotNetProjectWindow {
    param(
        [string]$Name,
        [string]$WorkingPath,
        [string]$ProjectFile
    )

    if (-not (Test-Path $WorkingPath)) { throw "$Name folder not found: $WorkingPath" }
    if (-not (Test-Path $ProjectFile)) { throw "$Name project file not found: $ProjectFile" }

    if ($Fast) {
        $runCommand = "dotnet run --no-build --project `"$ProjectFile`""
    }
    else {
        $runCommand = "dotnet restore `"$ProjectFile`"; if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }; dotnet build `"$ProjectFile`"; if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }; dotnet run --project `"$ProjectFile`""
    }

    $command = @"
`$host.UI.RawUI.WindowTitle = 'BirkNext $Name'
Set-Location '$WorkingPath'
Write-Host 'Starting BirkNext $Name...' -ForegroundColor Cyan
Write-Host 'Project: $ProjectFile' -ForegroundColor DarkGray
Write-Host ''
Write-Host 'When startup is complete, copy the shown localhost URL from this window.' -ForegroundColor Yellow
Write-Host ''
$runCommand
"@

    Info "Opening $Name window..."
    Start-Process powershell.exe -ArgumentList "-NoExit", "-ExecutionPolicy", "Bypass", "-Command", $command
}

Write-Host ""
Write-Host "============================================================"
Write-Host " BirkNext Local Startup"
Write-Host "============================================================"
Write-Host ""

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir

$backendPath = Join-Path $repoRoot "AIAssisted\backend"
$frontendPath = Join-Path $repoRoot "AIAssisted\frontend"

Info "Repository root: $repoRoot"
Info "Backend path:    $backendPath"
Info "Frontend path:   $frontendPath"
Info "Runtime:         $ContainerRuntime"
Info "Mode:            $(if ($Fast) { 'Fast' } else { 'Safe' })"
Info "Container delay: $ContainerDelaySeconds seconds"
Info "Backend delay:   $BackendDelaySeconds seconds"

Require-Command "dotnet" "Install .NET SDK 8 or add dotnet to PATH."

if (-not (Test-Path $backendPath)) { throw "Backend folder not found: $backendPath" }
if (-not (Test-Path $frontendPath)) { throw "Frontend folder not found: $frontendPath" }

$backendProject = Find-ProjectFile -BasePath $backendPath -PreferredName "BirkNext.Api"
$frontendProject = Find-ProjectFile -BasePath $frontendPath -PreferredName "BirkNext.Web"

Ok "Backend project:  $backendProject"
Ok "Frontend project: $frontendProject"

Push-Location $repoRoot

try {
    Start-ContainerServices

    if (-not $FrontendOnly) {
        Start-DotNetProjectWindow -Name "Backend" -WorkingPath $backendPath -ProjectFile $backendProject

        if (-not $BackendOnly -and $BackendDelaySeconds -gt 0) {
            Info "Waiting $BackendDelaySeconds seconds before starting frontend..."
            Start-Sleep -Seconds $BackendDelaySeconds
        }
    }
    else {
        Warn "FrontendOnly selected. Backend will not be started."
    }

    if (-not $BackendOnly) {
        Start-DotNetProjectWindow -Name "Frontend" -WorkingPath $frontendPath -ProjectFile $frontendProject
    }
    else {
        Warn "BackendOnly selected. Frontend will not be started."
    }

    Write-Host ""
    Ok "Startup triggered."
    Write-Host ""
    Write-Host "How to access the frontend:" -ForegroundColor Cyan
    Write-Host "  1. Look in the 'BirkNext Frontend' window."
    Write-Host "  2. Find the line that says something like:"
    Write-Host "       Now listening on: https://localhost:xxxx"
    Write-Host "  3. Open that URL in your browser."
    Write-Host ""
    Write-Host "Likely frontend URLs to try:" -ForegroundColor Cyan
    Write-Host "  https://localhost:5001"
    Write-Host "  https://localhost:7001"
    Write-Host "  https://localhost:7043"
    Write-Host ""
    Write-Host "Useful paths after frontend opens:" -ForegroundColor Cyan
    Write-Host "  /extract"
    Write-Host "  /scenarios"
    Write-Host ""
    Write-Host "Backend GraphQL is usually available at:" -ForegroundColor Cyan
    Write-Host "  https://localhost:<backend-port>/graphql"
    Write-Host ""
}
catch {
    Fail $_.Exception.Message
    Write-Host ""
    Write-Host "Troubleshooting:"
    Write-Host "  1. Check Podman/Docker is running"
    Write-Host "  2. Check .NET SDK 8 is installed"
    Write-Host "  3. Try: .\start-local.ps1 -SkipContainers"
    Write-Host "  4. Try longer delays:"
    Write-Host "       .\start-local.ps1 -ContainerDelaySeconds 20 -BackendDelaySeconds 30"
    Write-Host "  5. If project detection fails, verify .csproj names under AIAssisted\backend and AIAssisted\frontend"
    throw
}
finally {
    Pop-Location
}

Write-Host "Press Enter to close this launcher window..."
[void][System.Console]::ReadLine()
