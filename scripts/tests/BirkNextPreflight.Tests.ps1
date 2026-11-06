# Self-contained tests for scripts/BirkNextPreflight.psm1 (no Pester dependency: runs on Windows PowerShell 5.1 and pwsh on CI agents).
# Only simulated command results are used — no container runtime, database or network is touched. Exit code = number of failures.
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "..\BirkNextPreflight.psm1") -Force

$script:failures = 0
function Check([string]$name, [bool]$condition, [string]$detail = "") {
    if ($condition) { Write-Host "PASS $name" -ForegroundColor Green }
    else { $script:failures++; Write-Host "FAIL $name $detail" -ForegroundColor Red }
}

# Simulated "<runtime> info": podman/docker each Missing, Stopped (installed, exit 125) or Healthy (exit 0).
function Probe([string]$podman, [string]$docker) {
    $states = @{ podman = $podman; docker = $docker }
    return { param($runtime) switch ($states[$runtime]) {
        "Missing" { New-ProbeResult $false }
        "Stopped" { New-ProbeResult $true 125 "Cannot connect" }
        default { New-ProbeResult $true 0 "ok" } } }.GetNewClosure()
}

# ── Container runtime selection ──────────────────────────────────────────────────────────────────────────────
$r = Select-ContainerRuntime -Probe (Probe "Missing" "Missing")
Check "no Podman or Docker: clear failure that names both and the existing-PostgreSQL option" (-not $r.Ok -and $r.Message -match "neither Podman nor Docker" -and $r.Message -match "SkipContainers") $r.Message

$r = Select-ContainerRuntime -Probe (Probe "Stopped" "Missing")
Check "Podman installed, machine stopped: Podman, and the launcher starts the machine" ($r.Ok -and $r.Runtime -eq "podman" -and $r.StartPodmanMachine) $r.Message

$r = Select-ContainerRuntime -Probe (Probe "Missing" "Healthy")
Check "only Docker, healthy: Docker is selected automatically" ($r.Ok -and $r.Runtime -eq "docker" -and -not $r.StartPodmanMachine) $r.Message

$r = Select-ContainerRuntime -Probe (Probe "Healthy" "Healthy")
Check "both healthy: Podman (the documented default) is preferred" ($r.Ok -and $r.Runtime -eq "podman") $r.Message

$r = Select-ContainerRuntime -Probe (Probe "Stopped" "Healthy")
Check "Podman stopped, Docker healthy: the healthy runtime is used instead of starting a machine" ($r.Ok -and $r.Runtime -eq "docker") $r.Message

$r = Select-ContainerRuntime -Probe (Probe "Missing" "Stopped")
Check "Docker installed but not running: tells the tester to start Docker Desktop" (-not $r.Ok -and $r.Message -match "Start Docker Desktop") $r.Message

$r = Select-ContainerRuntime -Requested "docker" -Probe (Probe "Healthy" "Missing")
Check "explicit Docker without Docker installed: explicit choice is respected and explained, never silently switched" (-not $r.Ok -and $r.Message -match "Docker was chosen") $r.Message

$r = Select-ContainerRuntime -Requested "podman" -Probe (Probe "Missing" "Healthy")
Check "explicit Podman without Podman: explained with the alternative" (-not $r.Ok -and $r.Message -match "Podman was chosen" -and $r.Message -match "-ContainerRuntime docker") $r.Message

$r = Select-ContainerRuntime -EnvironmentChoice "docker" -Probe (Probe "Healthy" "Healthy")
Check "BIRKNEXT_CONTAINER_RUNTIME=docker wins over auto-detection" ($r.Ok -and $r.Runtime -eq "docker") $r.Message

$r = Select-ContainerRuntime -EnvironmentChoice "nonsense" -Probe (Probe "Missing" "Healthy")
Check "an unsupported environment value falls back to auto-detection" ($r.Ok -and $r.Runtime -eq "docker") $r.Message

# ── .NET runtime ─────────────────────────────────────────────────────────────────────────────────────────────
Check "dotnet missing" (-not (Test-DotNetRuntime $null).Ok)
Check ".NET 8 ASP.NET Core present" ((Test-DotNetRuntime "Microsoft.NETCore.App 8.0.11 [C:\x]`r`nMicrosoft.AspNetCore.App 8.0.11 [C:\x]").Ok)
$m = Test-DotNetRuntime "Microsoft.AspNetCore.App 9.0.1 [C:\x]"
Check "only .NET 9: missing .NET 8 is reported with the download page" (-not $m.Ok -and $m.Message -match "dotnet.microsoft.com") $m.Message

# ── Writable folders (paths with spaces; read-only) ──────────────────────────────────────────────────────────
$base = Join-Path ([IO.Path]::GetTempPath()) ("BirkNext preflight " + [guid]::NewGuid().ToString("N"))
$withSpaces = Join-Path $base "Tester Package\logs"
Check "a folder whose path contains spaces is writable and created" ((Test-WritableFolder $withSpaces).Ok -and (Test-Path -LiteralPath $withSpaces))
$onWindows = $PSVersionTable.PSEdition -eq "Desktop" -or ((Get-Variable IsWindows -ErrorAction SilentlyContinue) -and $IsWindows)
if ($onWindows) {
    $readOnly = Join-Path $base "readonly"
    New-Item -ItemType Directory -Force -Path $readOnly | Out-Null
    $acl = Get-Acl $readOnly
    $deny = New-Object System.Security.AccessControl.FileSystemAccessRule([System.Security.Principal.WindowsIdentity]::GetCurrent().Name, "Write,CreateFiles", "ContainerInherit,ObjectInherit", "None", "Deny")
    $acl.AddAccessRule($deny); Set-Acl $readOnly $acl
    $w = Test-WritableFolder $readOnly
    Check "a read-only folder is reported in plain language" (-not $w.Ok -and $w.Message -match "cannot write") $w.Message
    $acl.RemoveAccessRule($deny) | Out-Null; Set-Acl $readOnly $acl
}
else { Write-Host "SKIP read-only folder (Windows ACL test; this agent is not Windows)" -ForegroundColor Yellow }

# ── Independent installations: two copies keep separate writable data ────────────────────────────────────────
$copyA = Join-Path $base "Tester A\BirkNext"; $copyB = Join-Path $base "Tester B\BirkNext"
Test-WritableFolder (Join-Path $copyA "logs") | Out-Null; Test-WritableFolder (Join-Path $copyB "logs") | Out-Null
Set-Content (Join-Path $copyA "logs\marker.txt") "A"
Check "two extracted copies never share a log folder" (-not (Test-Path (Join-Path $copyB "logs\marker.txt")))
Remove-Item -LiteralPath $base -Recurse -Force

# ── Ports ────────────────────────────────────────────────────────────────────────────────────────────────────
Check "a free port is only 'available', never 'database running'" ((Get-PortStatusMessage 5432 "PostgreSQL" $false) -notmatch "running")
Check "an occupied port names its owner and the next step" ((Get-PortStatusMessage 5173 "frontend" $true "dotnet (PID 42)") -match "dotnet \(PID 42\).*start BirkNext again")

Write-Host ""
Write-Host ("{0} failure(s)" -f $script:failures)
exit $script:failures
