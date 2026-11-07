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

# ── Backend address (BQA-016): one resolution for display, preflight and launch ──────────────────────────────
$e = Resolve-BackendEndpoint -Kind Dll
Check "package without overrides: Kestrel's default http://localhost:5000 is checked" ($e.Ok -and $e.Port -eq 5000 -and $e.Source -eq "default") $e.Message
$e = Resolve-BackendEndpoint -Kind Dll -EnvironmentUrls "http://localhost:5080;http://127.0.0.1:5081"
Check "package with ASPNETCORE_URLS: its first URL wins, no hard-coded 5000" ($e.Ok -and $e.Port -eq 5080 -and $e.Source -eq "ASPNETCORE_URLS") $e.Message
$e = Resolve-BackendEndpoint -Kind Dll -AppSettingsUrls "http://[::1]:5090"
Check "package with appsettings Urls (IPv6 loopback): that port is checked" ($e.Ok -and $e.Port -eq 5090 -and $e.Url -eq "http://[::1]:5090") $e.Message
$e = Resolve-BackendEndpoint -Kind Dll -EnvironmentUrls "http://localhost:5080" -AppSettingsUrls "http://localhost:5090"
Check "ASPNETCORE_URLS overrides appsettings, as in ASP.NET Core" ($e.Port -eq 5080)
$e = Resolve-BackendEndpoint -Kind Project -LaunchSettingsUrl "http://localhost:5000" -EnvironmentUrls "http://localhost:5080"
Check "source checkout: the launch profile that dotnet run applies" ($e.Ok -and $e.Port -eq 5000 -and $e.Source -eq "launch profile") $e.Message
$e = Resolve-BackendEndpoint -Kind Project
Check "source checkout without applicationUrl: reported, not silently skipped" (-not $e.Ok -and $e.Message -match "cannot be checked") $e.Message
$e = Resolve-BackendEndpoint -Kind Dll -EnvironmentUrls "not a url"
Check "an invalid address is reported with where it came from" (-not $e.Ok -and $e.Message -match "ASPNETCORE_URLS") $e.Message

# ── PostgreSQL readiness (BQA-017): bounded, real readiness, never a fixed sleep ─────────────────────────────
# Simulated probe answers, one per attempt; the last answer repeats.
function Seq([object[]]$states) {
    $queue = [System.Collections.Queue]::new($states)
    return { param($attempt) if ($queue.Count -gt 1) { $queue.Dequeue() } else { $queue.Peek() } }.GetNewClosure()
}
$script:slept = 0
$noWait = { param($s) $script:slept += $s }
$running = @{ Container = "running"; Ready = $false; Detail = "/var/run/postgresql:5432 - no response" }
$ready = @{ Container = "running"; Ready = $true; Detail = "accepting connections" }

$script:slept = 0
$w = Wait-DatabaseReady -Probe (Seq @($ready)) -Sleep $noWait
Check "immediately ready: Ready on the first attempt, no waiting" ($w.State -eq "Ready" -and $w.Attempts -eq 1 -and $script:slept -eq 0) "$($w.State) $($w.Attempts)"
$script:slept = 0
$w = Wait-DatabaseReady -Probe (Seq @(@{ Container = "starting"; Ready = $false }, $running, $running, $ready)) -Sleep $noWait -IntervalSeconds 2
Check "delayed readiness: waits only until pg_isready succeeds" ($w.State -eq "Ready" -and $w.Attempts -eq 4 -and $script:slept -eq 6) "$($w.State) $($w.Attempts) slept=$script:slept"
$script:slept = 0
$w = Wait-DatabaseReady -Probe (Seq @($running)) -Sleep $noWait -TimeoutSeconds 10 -IntervalSeconds 2
Check "never ready: TimedOut within the bound, with the last pg_isready answer" ($w.State -eq "TimedOut" -and $w.Attempts -eq 5 -and $script:slept -eq 8 -and $w.Message -match "10 seconds" -and $w.Message -match "no response") $w.Message
$w = Wait-DatabaseReady -Probe (Seq @($running, @{ Container = "exited"; Ready = $false; Detail = "exit code 1" })) -Sleep $noWait
Check "container exits while starting: Failed at once, not after the timeout" ($w.State -eq "Failed" -and $w.Attempts -eq 2 -and $w.Message -match "stopped") $w.Message
$w = Wait-DatabaseReady -Probe (Seq @(@{ Container = "missing"; Ready = $false })) -Sleep $noWait
Check "no BirkNext postgres container: Failed (an unrelated database on the port never counts)" ($w.State -eq "Failed" -and $w.Message -match "not found") $w.Message
$w = Wait-DatabaseReady -Probe (Seq @(@{ Container = "error"; Ready = $false; Detail = "Cannot connect to Podman" })) -Sleep $noWait
Check "runtime stops answering: Failed after three command failures" ($w.State -eq "Failed" -and $w.Attempts -eq 3 -and $w.Message -match "did not answer") $w.Message
$w = Wait-DatabaseReady -Probe (Seq @(@{ Container = "error"; Ready = $false }, $running, @{ Container = "error"; Ready = $false }, $ready)) -Sleep $noWait
Check "one transient command failure does not fail the wait" ($w.State -eq "Ready") $w.State
Check "the readiness messages never contain a password" (-not ("$($w.Message)" -match "Password="))

# ── Container probe: identity by compose labels, state, pg_isready inside the container ──────────────────────
# Simulated "<runtime> <args>": answers by sub-command; every call is recorded so the identity filter and the absence of secrets are checked.
function Runtime([hashtable]$answers) {
    $calls = [System.Collections.ArrayList]::new()
    $run = { param($exe, $arguments) [void]$calls.Add("$exe $($arguments -join ' ')"); $a = $answers[$arguments[0]]; if ($a) { $a } else { @{ ExitCode = 0; Stdout = "" } } }.GetNewClosure()
    return [pscustomobject]@{ Run = $run; Calls = $calls }
}
$probeArgs = @{ Runtime = "podman"; Project = "birknext-studio-local"; User = "birknext"; Database = "birknext" }
$rt = Runtime @{ ps = @{ ExitCode = 0; Stdout = "abc123`n" }; inspect = @{ ExitCode = 0; Stdout = "running`n" }; exec = @{ ExitCode = 0; Stdout = "/var/run/postgresql:5432 - accepting connections" } }
$s = Get-PostgresContainerState @probeArgs -Run $rt.Run
Check "probe: running container whose pg_isready succeeds is Ready" ($s.Container -eq "running" -and $s.Ready) $s.Detail
Check "probe: the container is found by compose project AND service label, never by port" ($rt.Calls[0] -match "label=com.docker.compose.project=birknext-studio-local" -and $rt.Calls[0] -match "label=com.docker.compose.service=postgres") $rt.Calls[0]
Check "probe: pg_isready runs inside that container with no password" ($rt.Calls[2] -match "^podman exec abc123 pg_isready -U birknext -d birknext$" -and ($rt.Calls -join " ") -notmatch "PASSWORD|Password=") ($rt.Calls -join " | ")
$rt = Runtime @{ ps = @{ ExitCode = 0; Stdout = "abc123" }; inspect = @{ ExitCode = 0; Stdout = "running" }; exec = @{ ExitCode = 1; Stdout = "/var/run/postgresql:5432 - no response" } }
$s = Get-PostgresContainerState @probeArgs -Run $rt.Run
Check "probe: running but not accepting connections is not Ready" ($s.Container -eq "running" -and -not $s.Ready -and $s.Detail -match "no response") $s.Detail
$s = Get-PostgresContainerState @probeArgs -Run (Runtime @{ ps = @{ ExitCode = 0; Stdout = "abc123" }; inspect = @{ ExitCode = 0; Stdout = "created" } }).Run
Check "probe: a created/starting container is still starting" ($s.Container -eq "starting") $s.Detail
$s = Get-PostgresContainerState @probeArgs -Run (Runtime @{ ps = @{ ExitCode = 0; Stdout = "abc123" }; inspect = @{ ExitCode = 0; Stdout = "exited" } }).Run
Check "probe: an exited container is reported with where its log is" ($s.Container -eq "exited" -and $s.Detail -match "logs abc123") $s.Detail
$s = Get-PostgresContainerState @probeArgs -Run (Runtime @{ ps = @{ ExitCode = 0; Stdout = "" } }).Run
Check "probe: no container with BirkNext's labels (another database on the port) is missing" ($s.Container -eq "missing") $s.Detail
$s = Get-PostgresContainerState @probeArgs -Run (Runtime @{ ps = @{ ExitCode = 125; Stdout = "" } }).Run
Check "probe: the runtime command failing is an error, not 'missing'" ($s.Container -eq "error" -and $s.Detail -match "exit code 125") $s.Detail
$s = Get-PostgresContainerState -Runtime "docker" -Project "birknext-studio-local" -User "birknext" -Database "birknext" -Run (Runtime @{ ps = @{ ExitCode = 0; Stdout = "d0c" }; inspect = @{ ExitCode = 0; Stdout = "running" }; exec = @{ ExitCode = 0; Stdout = "accepting connections" } }).Run
Check "probe: Docker uses the same commands as Podman" ($s.Ready) $s.Detail

Write-Host ""
Write-Host ("{0} failure(s)" -f $script:failures)
exit $script:failures
