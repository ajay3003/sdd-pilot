param(
    [ValidateSet("podman", "docker")]
    [string]$ContainerRuntime = "podman",

    [string]$ComposeFile = "",

    [switch]$Fast,
    [switch]$SkipContainers,
    [switch]$BackendOnly,
    [switch]$FrontendOnly,

    [int]$ContainerDelaySeconds = 10,
    [int]$BackendDelaySeconds = 20,
    [int]$PodmanReadyRetries = 20,
    [int]$PodmanReadyDelaySeconds = 2
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

function Invoke-PodmanMachineCommand {
    param([string[]]$Arguments)

    $savedEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & podman @Arguments
    } finally {
        $ErrorActionPreference = $savedEAP
    }

    return $LASTEXITCODE
}

function Invoke-NativeCommand {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [switch]$ThrowOnError,
        [switch]$ShowOutput
    )

    $stdoutFile = [System.IO.Path]::GetTempFileName()
    $stderrFile = [System.IO.Path]::GetTempFileName()

    $argString = ($Arguments | ForEach-Object {
        if ($_ -match '\s') { "`"$_`"" } else { $_ }
    }) -join ' '

    try {
        $process = Start-Process `
            -FilePath $Executable `
            -ArgumentList $argString `
            -NoNewWindow `
            -RedirectStandardOutput $stdoutFile `
            -RedirectStandardError $stderrFile `
            -PassThru `
            -Wait

        $stdout = Get-Content $stdoutFile -Raw -ErrorAction SilentlyContinue
        $stderr = Get-Content $stderrFile -Raw -ErrorAction SilentlyContinue

        if ($ShowOutput -and -not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Host $stdout.TrimEnd()
        }

        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            foreach ($line in ($stderr -split "`r`n|`n")) {
                $trimmed = $line.Trim()
                if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                    Warn $trimmed
                }
            }
        }

        if ($ThrowOnError -and $process.ExitCode -ne 0) {
            throw "'$Executable $argString' failed with exit code $($process.ExitCode)"
        }

        return [PSCustomObject]@{
            ExitCode = $process.ExitCode
            Stdout   = if ($null -ne $stdout) { $stdout } else { "" }
            Stderr   = if ($null -ne $stderr) { $stderr } else { "" }
        }
    }
    finally {
        Remove-Item $stdoutFile -ErrorAction SilentlyContinue
        Remove-Item $stderrFile -ErrorAction SilentlyContinue
    }
}

function Test-PodmanReady {
    try {
        $result = Invoke-NativeCommand -Executable "podman" -Arguments @("info")
        return $result.ExitCode -eq 0
    }
    catch {
        return $false
    }
}

function Get-PodmanMachines {
    $result = Invoke-NativeCommand -Executable "podman" -Arguments @("machine", "list", "--format", "json")

    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Stdout)) {
        return @()
    }

    try {
        $machines = $result.Stdout | ConvertFrom-Json

        if ($null -eq $machines) {
            return @()
        }

        if ($machines -isnot [System.Array]) {
            return @($machines)
        }

        return $machines
    }
    catch {
        return @()
    }
}

function Ensure-PodmanReady {

    Require-Command "podman" "Install Podman Desktop or add podman to PATH."

    Info "Checking Podman..."

    if (Test-PodmanReady) {
        Ok "Podman is already ready"
        return
    }

    $machines = Get-PodmanMachines

    if ($machines.Count -eq 0) {

        Fail "No Podman machine found."

        Write-Host ""
        Write-Host "Run once manually:" -ForegroundColor Yellow
        Write-Host "  podman machine init"
        Write-Host "  podman machine start"

        throw "Podman machine missing."
    }

    $machine = $machines | Select-Object -First 1

    $machineName = $machine.Name

    if ([string]::IsNullOrWhiteSpace($machineName)) {
        $machineName = "podman-machine-default"
    }

    Info "Using Podman machine: $machineName"

    $isRunning = $false

    if ($null -ne $machine.Running) {
        $isRunning = [bool]$machine.Running
    }

    if (-not $isRunning) {

        Warn "Podman machine is stopped."
        Info "Starting Podman machine..."

        $exitCode = Invoke-PodmanMachineCommand -Arguments @("machine", "start", $machineName)

        if ($exitCode -ne 0) {
            throw "podman machine start failed with exit code $exitCode"
        }

        Start-Sleep -Seconds 5
    }
    else {

        Warn "Podman machine already running, but Podman not responding."
        Info "Restarting Podman machine..."

        Invoke-PodmanMachineCommand -Arguments @("machine", "stop", $machineName)

        Start-Sleep -Seconds 5

        $exitCode = Invoke-PodmanMachineCommand -Arguments @("machine", "start", $machineName)

        if ($exitCode -ne 0) {
            throw "podman machine start failed with exit code $exitCode"
        }

        Start-Sleep -Seconds 10
    }

    Info "Waiting for Podman to become ready..."

    for ($i = 1; $i -le $PodmanReadyRetries; $i++) {

        if (Test-PodmanReady) {

            Ok "Podman is ready"
            return
        }

        Info "Podman not ready yet. Retry $i/$PodmanReadyRetries..."

        Start-Sleep -Seconds $PodmanReadyDelaySeconds
    }

    Fail "Podman failed to become ready."

    Write-Host ""
    Write-Host "Try manually:" -ForegroundColor Yellow
    Write-Host "  podman machine stop"
    Write-Host "  podman machine start"
    Write-Host "  podman info"
    Write-Host ""
    Write-Host "Or restart Podman Desktop completely."

    throw "Podman readiness check failed."
}

function Find-ComposeFile {
    param([string]$Root)

    if (-not [string]::IsNullOrWhiteSpace($ComposeFile)) {
        $explicit = if ([System.IO.Path]::IsPathRooted($ComposeFile)) {
            $ComposeFile
        }
        else {
            Join-Path $Root $ComposeFile
        }

        if (Test-Path $explicit) {
            return (Resolve-Path $explicit).Path
        }

        throw "Compose file was specified but not found: $explicit"
    }

    $candidates = @(
        "compose.yaml",
        "compose.yml",
        "docker-compose.yml",
        "docker-compose.yaml",
        "infra\compose.yaml",
        "infra\compose.yml",
        "infra\docker-compose.yml",
        "infra\docker-compose.yaml",
        "AIAssisted\compose.yaml",
        "AIAssisted\compose.yml",
        "AIAssisted\docker-compose.yml",
        "AIAssisted\docker-compose.yaml"
    )

    foreach ($candidate in $candidates) {
        $path = Join-Path $Root $candidate

        if (Test-Path $path) {
            return (Resolve-Path $path).Path
        }
    }

    return $null
}

function Start-ContainerServices {
    if ($SkipContainers) {
        Warn "Skipping container startup."
        return
    }

    Info "Preparing container services using $ContainerRuntime..."

    $composePath = Find-ComposeFile -Root $repoRoot

    if ($null -eq $composePath) {
        Fail "No compose file found."
        Write-Host "Expected one of:" -ForegroundColor Yellow
        Write-Host "  compose.yaml"
        Write-Host "  docker-compose.yml"
        Write-Host "  infra\compose.yaml"
        Write-Host "  AIAssisted\docker-compose.yml"
        Write-Host ""
        Write-Host "Or run with:" -ForegroundColor Yellow
        Write-Host "  .\start-local.ps1 -SkipContainers"
        throw "Container startup cannot continue without a compose file."
    }

    Ok "Compose file: $composePath"

    if ($ContainerRuntime -eq "podman") {
        Ensure-PodmanReady

        if (Command-Exists "podman-compose") {
            Info "Running podman-compose..."
            $composeResult = Invoke-NativeCommand -Executable "podman-compose" -Arguments @("-f", $composePath, "up", "-d") -ShowOutput
        }
        else {
            Info "Running podman compose..."
            $composeResult = Invoke-NativeCommand -Executable "podman" -Arguments @("compose", "-f", $composePath, "up", "-d") -ShowOutput
        }

        if ($composeResult.ExitCode -ne 0) {
            throw "Podman compose failed."
        }

        Invoke-NativeCommand -Executable "podman" -Arguments @("ps") -ShowOutput
    }
    else {
        Require-Command "docker" "Install Docker Desktop or add docker to PATH."

        Info "Running docker compose..."
        $composeResult = Invoke-NativeCommand -Executable "docker" -Arguments @("compose", "-f", $composePath, "up", "-d") -ShowOutput

        if ($composeResult.ExitCode -ne 0) {
            throw "Docker compose failed."
        }

        Invoke-NativeCommand -Executable "docker" -Arguments @("ps") -ShowOutput
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

    if ($preferred) {
        return $preferred.FullName
    }

    $allProjects = Get-ChildItem -Path $BasePath -Recurse -Filter "*.csproj" -ErrorAction SilentlyContinue

    $nonTest = $allProjects |
        Where-Object { $_.FullName -notmatch "\.Tests\.csproj$" } |
        Select-Object -First 1

    if ($nonTest) {
        return $nonTest.FullName
    }

    throw "No runnable project file found under $BasePath"
}

function Start-DotNetProjectWindow {
    param(
        [string]$Name,
        [string]$WorkingPath,
        [string]$ProjectFile
    )

    if (-not (Test-Path $WorkingPath)) {
        throw "$Name folder not found: $WorkingPath"
    }

    if (-not (Test-Path $ProjectFile)) {
        throw "$Name project file not found: $ProjectFile"
    }

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

if (-not (Test-Path $backendPath)) {
    throw "Backend folder not found: $backendPath"
}

if (-not (Test-Path $frontendPath)) {
    throw "Frontend folder not found: $frontendPath"
}

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
    Write-Host "  2. Find: Now listening on: https://localhost:xxxx"
    Write-Host "  3. Open that URL in your browser."
    Write-Host ""
    Write-Host "Useful paths:" -ForegroundColor Cyan
    Write-Host "  /extract"
    Write-Host "  /scenarios"
}
catch {
    Fail $_.Exception.Message
    Write-Host ""
    Write-Host "Troubleshooting:" -ForegroundColor Yellow
    Write-Host "  podman machine list"
    Write-Host "  podman machine start"
    Write-Host "  podman machine init   # only if no machine exists"
    Write-Host "  .\start-local.ps1 -FrontendOnly -SkipContainers"
    throw
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Press Enter to close this launcher window..."
[void][System.Console]::ReadLine()
