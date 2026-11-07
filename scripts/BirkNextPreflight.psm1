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

<#
.SYNOPSIS
The one backend address the launcher displays, checks before starting anything, and starts the backend on.
.DESCRIPTION
Source checkout ("Project"): the launch profile's applicationUrl, which dotnet run applies. Published package ("Dll"): ASPNETCORE_URLS, then
the packaged appsettings "Urls", then Kestrel's default http://localhost:5000. Only the first URL is used (the launcher starts one backend).
#>
function Resolve-BackendEndpoint {
    param(
        [ValidateSet("Project", "Dll")][string]$Kind,
        [string]$LaunchSettingsUrl = "",
        [string]$EnvironmentUrls = "",
        [string]$AppSettingsUrls = ""
    )
    function Candidate([string]$value, [string]$source) { [pscustomobject]@{ Value = $value; Source = $source } }
    $candidates = if ($Kind -eq "Project") { @(Candidate $LaunchSettingsUrl "launch profile") }
        else { @((Candidate $EnvironmentUrls "ASPNETCORE_URLS"), (Candidate $AppSettingsUrls "appsettings.json Urls"), (Candidate "http://localhost:5000" "default")) }
    foreach ($candidate in $candidates) {
        $first = (("$($candidate.Value)" -split ";") | ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -First 1)
        if (-not $first) { continue }
        $uri = $null
        if (-not [Uri]::TryCreate(($first -replace "://[*+]", "://localhost"), [UriKind]::Absolute, [ref]$uri) -or $uri.Port -le 0) {
            return [pscustomobject]@{ Ok = $false; Url = $first; Port = $null; Source = $candidate.Source; Message = "The backend address '$first' ($($candidate.Source)) is not a valid URL such as http://localhost:5000." }
        }
        return [pscustomobject]@{ Ok = $true; Url = $first; Port = $uri.Port; Source = $candidate.Source; Message = "Backend address: $first ($($candidate.Source))." }
    }
    return [pscustomobject]@{ Ok = $false; Url = $null; Port = $null; Source = "launch profile"; Message = "The backend launch profile has no applicationUrl, so its port cannot be checked before starting." }
}

<#
.SYNOPSIS
Waits, bounded, until PostgreSQL in BirkNext's own container accepts connections, instead of sleeping a fixed time.
.DESCRIPTION
-Probe is called once per attempt and returns @{ Container = "running" | "starting" | "exited" | "missing" | "error"; Ready = bool; Detail = text }:
the compose project's postgres container (never "whatever answers on the port") and pg_isready inside it. -Sleep is injectable for tests.
States: Ready, TimedOut, Failed. Three runtime command failures in a row are a failure, not a reason to keep waiting.
#>
function Wait-DatabaseReady {
    param(
        [Parameter(Mandatory)][scriptblock]$Probe,
        [int]$TimeoutSeconds = 90,
        [int]$IntervalSeconds = 2,
        [scriptblock]$Sleep = { param($seconds) Start-Sleep -Seconds $seconds },
        [scriptblock]$OnWaiting = { param($attempt, $detail) }
    )
    $attempts = [math]::Max(1, [math]::Ceiling($TimeoutSeconds / [math]::Max(1, $IntervalSeconds)))
    $errors = 0; $last = ""
    function Result([string]$state, [int]$attempt, [string]$message) { [pscustomobject]@{ State = $state; Attempts = $attempt; Message = $message } }
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        $p = & $Probe $attempt
        # A probe answer is a hashtable or an object; absent fields read as empty under strict mode.
        $field = { param($name) if ($p -is [System.Collections.IDictionary]) { $p[$name] } elseif ($p.PSObject.Properties[$name]) { $p.$name } }
        $last = "$(& $field 'Detail')"
        switch ("$(& $field 'Container')") {
            "missing" { return Result "Failed" $attempt "The BirkNext PostgreSQL container was not found after compose started it. $last" }
            "exited" { return Result "Failed" $attempt "The BirkNext PostgreSQL container stopped while starting. $last" }
            "error" {
                $errors++
                if ($errors -ge 3) { return Result "Failed" $attempt "The container runtime did not answer while BirkNext waited for the database. $last" }
            }
            default {
                $errors = 0
                if (& $field 'Ready') { return Result "Ready" $attempt "PostgreSQL accepts connections." }
            }
        }
        if ($attempt -lt $attempts) { & $OnWaiting $attempt $last; & $Sleep $IntervalSeconds }
    }
    return Result "TimedOut" $attempts "PostgreSQL did not accept connections within $TimeoutSeconds seconds. $last"
}

<#
.SYNOPSIS
One readiness answer for Wait-DatabaseReady: BirkNext's own postgres container, found by its compose project and service labels (an unrelated
database answering on the port never counts), its state, and pg_isready inside it. No password is needed or printed.
-Run executes "<exe> <arguments>" and returns @{ ExitCode; Stdout }.
#>
function Get-PostgresContainerState {
    param(
        [Parameter(Mandatory)][string]$Runtime,
        [Parameter(Mandatory)][string]$Project,
        [Parameter(Mandatory)][string]$User,
        [Parameter(Mandatory)][string]$Database,
        [Parameter(Mandatory)][scriptblock]$Run
    )
    $ids = & $Run $Runtime @("ps", "-a", "-q", "--filter", "label=com.docker.compose.project=$Project", "--filter", "label=com.docker.compose.service=postgres")
    if ($ids.ExitCode -ne 0) { return @{ Container = "error"; Ready = $false; Detail = "$Runtime ps failed (exit code $($ids.ExitCode))." } }
    $id = @("$($ids.Stdout)" -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $id) { return @{ Container = "missing"; Ready = $false; Detail = "No container labelled com.docker.compose.project=$Project, service postgres." } }
    $status = & $Run $Runtime @("inspect", "--format", "{{.State.Status}}", $id)
    if ($status.ExitCode -ne 0) { return @{ Container = "error"; Ready = $false; Detail = "$Runtime inspect failed (exit code $($status.ExitCode))." } }
    $state = "$($status.Stdout)".Trim()
    if ($state -in @("exited", "dead", "stopped")) { return @{ Container = "exited"; Ready = $false; Detail = "Container state: $state. Its log: $Runtime logs $id" } }
    if ($state -ne "running") { return @{ Container = "starting"; Ready = $false; Detail = "Container state: $state." } }
    $check = & $Run $Runtime @("exec", $id, "pg_isready", "-U", $User, "-d", $Database)
    return @{ Container = "running"; Ready = ($check.ExitCode -eq 0); Detail = "pg_isready: $("$($check.Stdout)".Trim())" }
}

Export-ModuleMember -Function New-ProbeResult, Select-ContainerRuntime, Test-DotNetRuntime, Test-WritableFolder, Get-PortStatusMessage, Resolve-BackendEndpoint, Wait-DatabaseReady, Get-PostgresContainerState
