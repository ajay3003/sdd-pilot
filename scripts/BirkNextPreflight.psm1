# BirkNext tester-package preflight: pure decisions over injected probes, so every outcome is testable without touching a real container runtime.
# Used by start-local.ps1; tested by scripts/tests/BirkNextPreflight.Tests.ps1 (simulated commands only).

Set-StrictMode -Version Latest

# A probe result: whether the command exists and, when it ran, its exit code and output.
function New-ProbeResult([bool]$Exists, [int]$ExitCode = 0, [string]$Output = "") {
    [pscustomobject]@{ Exists = $Exists; ExitCode = $ExitCode; Output = $Output }
}

<#
.SYNOPSIS
Chooses the container runtime for PostgreSQL. Order: explicit -Requested (podman/docker), then $EnvironmentChoice (BIRKNEXT_CONTAINER_RUNTIME),
then a runtime that is installed AND healthy (Podman first, then Docker), then an installed Podman whose machine is stopped (the launcher starts
it). Docker installed but not running, or no runtime at all, is a clear failure with the next step. Never installs anything.
.PARAMETER Probe
Scriptblock { param($Runtime) } returning New-ProbeResult for "<runtime> info" (Exists=false when the command is not on PATH).
.OUTPUTS
[pscustomobject] Runtime ('podman'|'docker'|$null), StartPodmanMachine (bool), Message (plain language), Ok (bool).
#>
function Select-ContainerRuntime {
    param(
        [ValidateSet("auto", "podman", "docker")][string]$Requested = "auto",
        [string]$EnvironmentChoice = "",
        [Parameter(Mandatory)][scriptblock]$Probe
    )
    $choice = if ($Requested -ne "auto") { $Requested } elseif ($EnvironmentChoice -in @("podman", "docker")) { $EnvironmentChoice } else { "auto" }
    $podman = & $Probe "podman"
    $docker = & $Probe "docker"

    function Result($runtime, [bool]$start, [string]$message, [bool]$ok) {
        [pscustomobject]@{ Runtime = $runtime; StartPodmanMachine = $start; Message = $message; Ok = $ok }
    }

    if ($choice -eq "podman") {
        if (-not $podman.Exists) { return Result $null $false "Podman was chosen but is not installed (podman is not on PATH). Install Podman Desktop from https://podman-desktop.io, or start BirkNext with -ContainerRuntime docker if Docker Desktop is installed." $false }
        if ($podman.ExitCode -eq 0) { return Result "podman" $false "Using Podman." $true }
        return Result "podman" $true "Using Podman. Its machine is not running yet; BirkNext will start it." $true
    }
    if ($choice -eq "docker") {
        if (-not $docker.Exists) { return Result $null $false "Docker was chosen but is not installed (docker is not on PATH). Install Docker Desktop, or start BirkNext with -ContainerRuntime podman if Podman is installed." $false }
        if ($docker.ExitCode -eq 0) { return Result "docker" $false "Using Docker." $true }
        return Result $null $false "Docker is installed but not running. Start Docker Desktop, wait until it says it is running, then start BirkNext again." $false
    }
    # auto
    if ($podman.Exists -and $podman.ExitCode -eq 0) { return Result "podman" $false "Using Podman (installed and running)." $true }
    if ($docker.Exists -and $docker.ExitCode -eq 0) { return Result "docker" $false "Using Docker (installed and running)." $true }
    if ($podman.Exists) { return Result "podman" $true "Using Podman. Its machine is not running yet; BirkNext will start it." $true }
    if ($docker.Exists) { return Result $null $false "Docker is installed but not running. Start Docker Desktop, wait until it says it is running, then start BirkNext again." $false }
    return Result $null $false ("BirkNext needs a database (PostgreSQL). It runs it in a container, but neither Podman nor Docker is installed on this PC. " +
        "Install Podman Desktop (https://podman-desktop.io) or Docker Desktop, then start BirkNext again. " +
        "If PostgreSQL is already installed and running on port 5432, start BirkNext with -SkipContainers instead.") $false
}

<#
.SYNOPSIS
Whether the ASP.NET Core runtime major version the package needs is installed, from "dotnet --list-runtimes" output (null output = dotnet missing).
#>
function Test-DotNetRuntime {
    param([AllowNull()][string]$ListRuntimesOutput, [int]$Major = 8)
    if ($null -eq $ListRuntimesOutput) {
        return [pscustomobject]@{ Ok = $false; Message = ".NET is not installed (the dotnet command was not found). Install the .NET $Major ASP.NET Core Runtime from https://dotnet.microsoft.com/download/dotnet/$Major.0, then start BirkNext again." }
    }
    $found = @($ListRuntimesOutput -split "`r?`n" | Where-Object { $_ -match "^Microsoft\.AspNetCore\.App\s+$Major\." })
    if ($found.Count -gt 0) { return [pscustomobject]@{ Ok = $true; Message = ".NET $Major ASP.NET Core Runtime found." } }
    return [pscustomobject]@{ Ok = $false; Message = "The .NET $Major ASP.NET Core Runtime is missing (another .NET version may be installed). Install it from https://dotnet.microsoft.com/download/dotnet/$Major.0, then start BirkNext again." }
}

<#
.SYNOPSIS
Whether BirkNext can write where it keeps logs and local data (a downloaded package may be extracted into a read-only or protected folder).
#>
function Test-WritableFolder {
    param([Parameter(Mandatory)][string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
        $probe = Join-Path $Path (".birknext-write-test-" + [guid]::NewGuid().ToString("N"))
        Set-Content -LiteralPath $probe -Value "ok" -ErrorAction Stop
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Ok = $true; Message = "Writable: $Path" }
    }
    catch {
        return [pscustomobject]@{ Ok = $false; Message = "BirkNext cannot write to $Path. Extract the package to a folder you own (for example under Documents), not under Program Files or a read-only share." }
    }
}

<#
.SYNOPSIS
Plain-language description of a port conflict. A free port says nothing about a database running: it is only "available".
#>
function Get-PortStatusMessage {
    param([int]$Port, [string]$Purpose, [bool]$InUse, [string]$Owner = "")
    if (-not $InUse) { return "Port $Port ($Purpose) is available." }
    $who = if ($Owner) { " by $Owner" } else { "" }
    return "Port $Port ($Purpose) is already in use$who. Close that program, or stop the other BirkNext copy, then start BirkNext again."
}

Export-ModuleMember -Function New-ProbeResult, Select-ContainerRuntime, Test-DotNetRuntime, Test-WritableFolder, Get-PortStatusMessage
