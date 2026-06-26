param(
    [ValidateSet("podman", "docker")]
    [string]$ContainerRuntime = "podman",

    [string]$ComposeFile = "",

    [switch]$Fast,
    [switch]$SkipContainers,
    [switch]$BackendOnly,
    [switch]$FrontendOnly,
    [switch]$ForceKillPorts,

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

    # Important: do not let podman stdout/stderr become part of the function return value.
    # PowerShell returns every pipeline output from a function, not only the value after `return`.
    # Without this, messages like `Machine "podman-machine-default" started successfully`
    # are captured together with $LASTEXITCODE, and callers may treat the command as failed.
    $savedEAP = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & podman @Arguments 2>&1
        $exitCode = [int]$LASTEXITCODE

        if ($null -ne $output) {
            $output | ForEach-Object { Write-Host $_ }
        }

        return $exitCode
    }
    finally {
        $ErrorActionPreference = $savedEAP
    }
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

function Load-EnvFile {
    param([string]$Path)

    $values = @{}

    if (-not (Test-Path $Path)) {
        Warn "Environment file not found: $Path"
        return $values
    }

    foreach ($line in Get-Content $Path) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
            continue
        }

        $separator = $trimmed.IndexOf("=")
        if ($separator -lt 1) {
            continue
        }

        $name = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1).Trim().Trim('"').Trim("'")
        $values[$name] = $value
        [Environment]::SetEnvironmentVariable($name, $value, "Process")
    }

    return $values
}

function Escape-PowerShellSingleQuotedString {
    param([string]$Value)
    return $Value.Replace("'", "''")
}

function Get-ProjectLaunchPort {
    param(
        [string]$ProjectFile,
        [string]$ProfileName = "http"
    )

    $projectDir = Split-Path -Parent $ProjectFile
    $launchSettingsPath = Join-Path $projectDir "Properties\launchSettings.json"

    if (-not (Test-Path $launchSettingsPath)) {
        return $null
    }

    try {
        $launchSettings = Get-Content $launchSettingsPath -Raw | ConvertFrom-Json
        $profile = $launchSettings.profiles.$ProfileName

        if ($null -eq $profile -or [string]::IsNullOrWhiteSpace($profile.applicationUrl)) {
            return $null
        }

        $firstUrl = ($profile.applicationUrl -split ";") | Select-Object -First 1
        $uri = [System.Uri]$firstUrl
        return $uri.Port
    }
    catch {
        Warn "Could not read launch port from $launchSettingsPath"
        return $null
    }
}

function Get-PortOwnerProcess {
    param([int]$Port)

    $processIds = @()

    if (Command-Exists "Get-NetTCPConnection") {
        try {
            $processIds += Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop |
                Select-Object -ExpandProperty OwningProcess -Unique
        }
        catch {
            $processIds += @()
        }
    }

    if ($processIds.Count -eq 0) {
        try {
            $netstatOutput = & netstat -ano -p tcp 2>$null

            foreach ($line in $netstatOutput) {
                if ($line -notmatch "LISTENING") {
                    continue
                }

                $columns = $line.Trim() -split "\s+"

                if ($columns.Count -lt 5) {
                    continue
                }

                $localAddress = $columns[1]
                $pidText = $columns[-1]

                if ($localAddress -match "[:.]$Port$" -and $pidText -match "^\d+$") {
                    $processIds += [int]$pidText
                }
            }
        }
        catch {
            $processIds += @()
        }
    }

    $owners = @()

    foreach ($processId in ($processIds | Where-Object { $_ -gt 0 } | Select-Object -Unique)) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        $commandLine = ""

        try {
            $cimProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $processId" -ErrorAction Stop
            $commandLine = if ($null -ne $cimProcess.CommandLine) { $cimProcess.CommandLine } else { "" }
        }
        catch {
            $commandLine = ""
        }

        $owners += [PSCustomObject]@{
            Id          = $processId
            ProcessName = if ($null -ne $process) { $process.ProcessName } else { "unknown" }
            Path        = if ($null -ne $process) { $process.Path } else { "" }
            CommandLine = $commandLine
        }
    }

    return $owners
}

function Test-PortInUse {
    param([int]$Port)
    return @(Get-PortOwnerProcess -Port $Port).Count -gt 0
}

function Test-IsAllowedLocalProcess {
    param(
        $Owner,
        [string]$RepoRoot,
        [string]$ProjectFile
    )

    $processName = if ($null -ne $Owner.ProcessName) { $Owner.ProcessName.ToLowerInvariant() } else { "" }
    $commandLine = if ($null -ne $Owner.CommandLine) { $Owner.CommandLine } else { "" }
    $normalizedRepoRoot = $RepoRoot.ToLowerInvariant()
    $normalizedProjectFile = $ProjectFile.ToLowerInvariant()
    $normalizedCommandLine = $commandLine.ToLowerInvariant()

    if ($processName -ne "dotnet") {
        return $false
    }

    if (
        $normalizedCommandLine.Contains($normalizedProjectFile) -or
        ($normalizedCommandLine.Contains("birknext") -and $normalizedCommandLine.Contains($normalizedRepoRoot))
    ) {
        return $true
    }

    return [string]::IsNullOrWhiteSpace($commandLine)
}

function Stop-PortOwnerIfAllowed {
    param(
        [string]$Name,
        [int]$Port,
        [string]$RepoRoot,
        [string]$ProjectFile
    )

    $owners = @(Get-PortOwnerProcess -Port $Port)

    if ($owners.Count -eq 0) {
        Ok "$Name port $Port is available"
        return $true
    }

    Warn "Port $Port is already in use"

    foreach ($owner in $owners) {
        $summary = "PID $($owner.Id) ($($owner.ProcessName))"
        Write-Host "  Owner: $summary"

        if (-not [string]::IsNullOrWhiteSpace($owner.CommandLine)) {
            Write-Host "  Command: $($owner.CommandLine)" -ForegroundColor DarkGray
        }
    }

    Write-Host ""

    if (-not $ForceKillPorts) {
        Warn "$Name will not be started while port $Port is busy."
        Write-Host "Inspect the port:" -ForegroundColor Yellow
        Write-Host "  netstat -ano | findstr :$Port"
        Write-Host "Stop a known stale process:" -ForegroundColor Yellow
        Write-Host "  taskkill /PID <pid> /F"
        Write-Host "Or rerun local startup with:" -ForegroundColor Yellow
        Write-Host "  .\scripts\start-local.ps1 -ForceKillPorts"
        Write-Host ""
        return $false
    }

    foreach ($owner in $owners) {
        $isAllowed = Test-IsAllowedLocalProcess -Owner $owner -RepoRoot $RepoRoot -ProjectFile $ProjectFile

        if (-not $isAllowed) {
            Fail "Refusing to kill PID $($owner.Id) because it does not look like a local BirkNext dotnet process."
            Write-Host "Inspect manually:" -ForegroundColor Yellow
            Write-Host "  netstat -ano | findstr :$Port"
            Write-Host "  taskkill /PID $($owner.Id) /F"
            return $false
        }

        if ([string]::IsNullOrWhiteSpace($owner.CommandLine)) {
            Warn "Command line for PID $($owner.Id) is not available; treating dotnet on configured $Name port $Port as a stale local process."
        }

        Info "Stopping stale $Name port owner PID $($owner.Id) ($($owner.ProcessName))..."
        Stop-Process -Id $owner.Id -Force -ErrorAction Stop
    }

    Start-Sleep -Seconds 1

    if (Test-PortInUse -Port $Port) {
        Fail "Port $Port is still in use after cleanup."
        return $false
    }

    Ok "$Name port $Port is available"
    return $true
}

function Get-PostgresConfig {
    param([hashtable]$EnvValues)

    return [PSCustomObject]@{
        Host     = if ($EnvValues.ContainsKey("POSTGRES_HOST")) { $EnvValues["POSTGRES_HOST"] } else { "localhost" }
        Port     = if ($EnvValues.ContainsKey("POSTGRES_PORT")) { $EnvValues["POSTGRES_PORT"] } else { "5432" }
        Database = if ($EnvValues.ContainsKey("POSTGRES_DB")) { $EnvValues["POSTGRES_DB"] } else { "birknext" }
        User     = if ($EnvValues.ContainsKey("POSTGRES_USER")) { $EnvValues["POSTGRES_USER"] } else { "birknext" }
        Password = if ($EnvValues.ContainsKey("POSTGRES_PASSWORD")) { $EnvValues["POSTGRES_PASSWORD"] } else { "birknext" }
    }
}

function Get-BackendConnectionString {
    param($PostgresConfig)

    return "Host=$($PostgresConfig.Host);Port=$($PostgresConfig.Port);Database=$($PostgresConfig.Database);Username=$($PostgresConfig.User);Password=$($PostgresConfig.Password)"
}

function Get-SanitizedConnectionString {
    param($PostgresConfig)

    return "Host=$($PostgresConfig.Host);Port=$($PostgresConfig.Port);Database=$($PostgresConfig.Database);Username=$($PostgresConfig.User);Password=***"
}

function Test-PostgresConnection {
    param(
        [string]$DbHost = "localhost",
        [int]$Port = 5432,
        [int]$TimeoutMs = 3000
    )

    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $asyncResult = $client.BeginConnect($DbHost, $Port, $null, $null)
        $connected = $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMs)

        if ($connected -and $client.Connected) {
            $client.Close()
            return $true
        }

        try { $client.Close() } catch {}
        return $false
    }
    catch {
        return $false
    }
}

function Detect-ExistingBirkNextDatabase {
    param([int]$Port = 5432)

    $projectName = if (-not [string]::IsNullOrWhiteSpace($env:COMPOSE_PROJECT_NAME)) {
        $env:COMPOSE_PROJECT_NAME
    }
    else {
        "birknext-studio-local"
    }

    $runtimesToCheck = @()
    if (Command-Exists "podman") { $runtimesToCheck += "podman" }
    if (Command-Exists "docker") { $runtimesToCheck += "docker" }

    foreach ($rt in $runtimesToCheck) {
        try {
            $filterArg = "label=com.docker.compose.project=$projectName"

            # Check ports of running containers in this compose project
            $portResult = Invoke-NativeCommand -Executable $rt -Arguments @("ps", "--filter", $filterArg, "--format", "{{.Ports}}")

            if ($portResult.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($portResult.Stdout)) {
                foreach ($line in ($portResult.Stdout -split "`r?`n")) {
                    $trimmed = $line.Trim()
                    if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
                    # Port mapping format: "0.0.0.0:5432->5432/tcp" or ":::5432->5432/tcp"
                    if ($trimmed -match ":${Port}->") {
                        return $true
                    }
                }

                # If containers are running but port format was not matched,
                # fall back to checking by container name
                $nameResult = Invoke-NativeCommand -Executable $rt -Arguments @("ps", "--filter", $filterArg, "--format", "{{.Names}}")
                if ($nameResult.ExitCode -eq 0 -and $nameResult.Stdout -match "postgres") {
                    return $true
                }
            }
        }
        catch {
            # Runtime unavailable or Podman machine offline — try next runtime
        }
    }

    return $false
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

function Show-PostgresPortConflictHelp {
    param([int]$Port)

    Write-Host ""
    Write-Host "Possible causes:" -ForegroundColor Yellow
    Write-Host "  * Existing BirkNext database container already running"
    Write-Host "  * Local PostgreSQL installed as a Windows service"
    Write-Host "  * Docker Desktop or another container using port $Port"
    Write-Host "  * Another development environment"
    Write-Host ""
    Write-Host "Suggested actions:" -ForegroundColor Yellow
    Write-Host "  1. If a BirkNext container is already running, skip container startup:"
    Write-Host "     .\scripts\start-local.ps1 -SkipContainers"
    Write-Host "  2. Stop the conflicting application and retry"
    Write-Host "  3. Investigate the port owner:"
    Write-Host "     netstat -ano | findstr :$Port"
    Write-Host ""
}

function Start-ContainerServices {
    if ($SkipContainers) {
        $composePath = Find-ComposeFile -Root $repoRoot
        if ($null -ne $composePath) {
            $composeEnvPath = Join-Path (Split-Path -Parent $composePath) ".env"
            $composeEnv = Load-EnvFile -Path $composeEnvPath
            $script:PostgresConfig = Get-PostgresConfig -EnvValues $composeEnv
            $script:BackendConnectionString = Get-BackendConnectionString -PostgresConfig $script:PostgresConfig
            Info "PostgreSQL config: $(Get-SanitizedConnectionString -PostgresConfig $script:PostgresConfig)"
        }
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

    $composeEnvPath = Join-Path (Split-Path -Parent $composePath) ".env"
    $composeEnv = Load-EnvFile -Path $composeEnvPath
    $script:PostgresConfig = Get-PostgresConfig -EnvValues $composeEnv
    $script:BackendConnectionString = Get-BackendConnectionString -PostgresConfig $script:PostgresConfig

    Info "PostgreSQL config: $(Get-SanitizedConnectionString -PostgresConfig $script:PostgresConfig)"

    # ── PostgreSQL port availability check ───────────────────────────────────
    $pgPort = [int]$script:PostgresConfig.Port

    Info "Checking PostgreSQL port $pgPort..."

    if (Test-PortInUse -Port $pgPort) {
        Warn "PostgreSQL port $pgPort is already in use."

        $owners = @(Get-PortOwnerProcess -Port $pgPort)
        foreach ($owner in $owners) {
            Write-Host "  Owner: PID $($owner.Id) ($($owner.ProcessName))" -ForegroundColor DarkGray
            if (-not [string]::IsNullOrWhiteSpace($owner.CommandLine)) {
                Write-Host "  Command: $($owner.CommandLine)" -ForegroundColor DarkGray
            }
        }
        Write-Host ""

        # Check if our own BirkNext container is already running
        Info "Checking for existing BirkNext database container..."
        $isBirkNextContainer = Detect-ExistingBirkNextDatabase -Port $pgPort

        if ($isBirkNextContainer) {
            Info "Existing BirkNext database already running (Compose project: $env:COMPOSE_PROJECT_NAME)."
            Info "Reusing running PostgreSQL instance."
            Ok "Database ready."
            return
        }

        # Not our container — test whether a reachable PostgreSQL is on that port
        Info "Testing PostgreSQL connectivity on port $pgPort..."
        $canConnect = Test-PostgresConnection -DbHost $script:PostgresConfig.Host -Port $pgPort

        if ($canConnect) {
            Ok "Existing PostgreSQL instance is reachable on port $pgPort."
            Info "Continuing startup with existing database instance."
            Warn "The running database may not be the BirkNext database. Backend connection errors may occur if the database '$($script:PostgresConfig.Database)' or user '$($script:PostgresConfig.User)' does not exist on this instance."
            return
        }

        # Port occupied, not our container, and not reachable — fail with diagnostics
        Fail "Unable to start PostgreSQL container because port $pgPort is already in use and the existing service is not reachable."
        Show-PostgresPortConflictHelp -Port $pgPort
        throw "PostgreSQL port $pgPort conflict: port occupied and database not reachable."
    }
    else {
        Ok "PostgreSQL port $pgPort is available."
    }
    # ─────────────────────────────────────────────────────────────────────────

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
            if ($composeResult.Stderr -match "address already in use|port is already allocated|bind:") {
                Fail "Unable to start PostgreSQL container because port $pgPort is already in use."
                Show-PostgresPortConflictHelp -Port $pgPort
            }
            else {
                Fail "Podman compose failed (exit code $($composeResult.ExitCode))."
            }
            throw "Compose startup failed."
        }

        Invoke-NativeCommand -Executable "podman" -Arguments @("ps") -ShowOutput
    }
    else {
        Require-Command "docker" "Install Docker Desktop or add docker to PATH."

        Info "Running docker compose..."
        $composeResult = Invoke-NativeCommand -Executable "docker" -Arguments @("compose", "-f", $composePath, "up", "-d") -ShowOutput

        if ($composeResult.ExitCode -ne 0) {
            if ($composeResult.Stderr -match "address already in use|port is already allocated|bind:") {
                Fail "Unable to start PostgreSQL container because port $pgPort is already in use."
                Show-PostgresPortConflictHelp -Port $pgPort
            }
            else {
                Fail "Docker compose failed (exit code $($composeResult.ExitCode))."
            }
            throw "Compose startup failed."
        }

        Invoke-NativeCommand -Executable "docker" -Arguments @("ps") -ShowOutput
    }

    if ($ContainerDelaySeconds -gt 0) {
        Info "Waiting $ContainerDelaySeconds seconds for containers/database to initialize..."
        Start-Sleep -Seconds $ContainerDelaySeconds
    }
}

function Find-DotNetRunnable {
    param(
        [string]$BasePath,
        [string]$PreferredName
    )

    # Source repository mode: prefer .csproj and use dotnet run.
    $preferredProject = Get-ChildItem -Path $BasePath -Recurse -Filter "$PreferredName.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($preferredProject) {
        return [PSCustomObject]@{
            Kind = "Project"
            Path = $preferredProject.FullName
        }
    }

    $allProjects = Get-ChildItem -Path $BasePath -Recurse -Filter "*.csproj" -ErrorAction SilentlyContinue

    $nonTestProject = $allProjects |
        Where-Object { $_.FullName -notmatch "\.Tests\.csproj$" } |
        Select-Object -First 1

    if ($nonTestProject) {
        return [PSCustomObject]@{
            Kind = "Project"
            Path = $nonTestProject.FullName
        }
    }

    # Pipeline artifact mode: published output contains DLLs, not .csproj files.
    # Prefer the expected app DLL first, then fall back to the first non-test DLL.
    $preferredDll = Get-ChildItem -Path $BasePath -Recurse -Filter "$PreferredName.dll" -ErrorAction SilentlyContinue | Select-Object -First 1

    if ($preferredDll) {
        return [PSCustomObject]@{
            Kind = "Dll"
            Path = $preferredDll.FullName
        }
    }

    $allDlls = Get-ChildItem -Path $BasePath -Recurse -Filter "*.dll" -ErrorAction SilentlyContinue

    $nonTestDll = $allDlls |
        Where-Object {
            $_.Name -notmatch "\.Tests\.dll$" -and
            $_.Name -notmatch "^Microsoft\." -and
            $_.Name -notmatch "^System\."
        } |
        Select-Object -First 1

    if ($nonTestDll) {
        return [PSCustomObject]@{
            Kind = "Dll"
            Path = $nonTestDll.FullName
        }
    }

    throw "No runnable project file or published DLL found under $BasePath"
}

function Start-DotNetProcessLogged {
    param(
        [string]$Name,
        [string]$WorkingPath,
        [object]$Runnable,
        [string]$StdoutLog,
        [string]$StderrLog,
        [string]$LauncherLogFile
    )

    if (-not (Test-Path $WorkingPath)) {
        throw "$Name folder not found: $WorkingPath"
    }

    if (-not $Runnable -or -not (Test-Path $Runnable.Path)) {
        throw "$Name runnable target not found: $($Runnable.Path)"
    }

    # Set backend environment variables — inherited by the child process
    if ($Name -eq "Backend" -and -not [string]::IsNullOrWhiteSpace($script:BackendConnectionString)) {
        $env:ConnectionStrings__Default = $script:BackendConnectionString
        $env:POSTGRES_DB                = $script:PostgresConfig.Database
        $env:POSTGRES_USER              = $script:PostgresConfig.User
        $env:POSTGRES_PASSWORD          = $script:PostgresConfig.Password
        Info "Database: $(Get-SanitizedConnectionString -PostgresConfig $script:PostgresConfig)"
    }

    $argString = ""

    if ($Runnable.Kind -eq "StaticWeb") {
        # Blazor WASM artifact: serve wwwroot with dotnet-serve
        $toolsPath = "$env:USERPROFILE\.dotnet\tools"
        if ($env:PATH -notlike "*$toolsPath*") { $env:PATH += ";$toolsPath" }

        $toolCheck = Invoke-NativeCommand -Executable "dotnet" -Arguments @("tool", "list", "-g")
        if ($toolCheck.Stdout -notmatch "dotnet-serve") {
            Info "Installing dotnet-serve..."
            $null = Invoke-NativeCommand -Executable "dotnet" -Arguments @("tool", "install", "--global", "dotnet-serve") -ShowOutput -ThrowOnError
        }
        $argString = "serve --directory `"$($Runnable.Path)`" --port 5173"
    }
    elseif ($Runnable.Kind -eq "Dll") {
        $argString = "`"$($Runnable.Path)`""
    }
    elseif ($Fast) {
        $argString = "run --no-build --project `"$($Runnable.Path)`""
    }
    else {
        # Source mode: restore and build synchronously in the launcher window, then run in background
        Info "Restoring $Name..."
        Add-Content -Path $LauncherLogFile -Value "[$((Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))] Restoring $Name"
        $null = Invoke-NativeCommand -Executable "dotnet" -Arguments @("restore", $Runnable.Path) -ShowOutput -ThrowOnError

        Info "Building $Name..."
        Add-Content -Path $LauncherLogFile -Value "[$((Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))] Building $Name"
        $null = Invoke-NativeCommand -Executable "dotnet" -Arguments @("build", $Runnable.Path, "--no-restore") -ShowOutput -ThrowOnError

        Ok "$Name built successfully."
        $argString = "run --no-build --project `"$($Runnable.Path)`""
    }

    Add-Content -Path $LauncherLogFile -Value "[$((Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))] Starting ${Name}: dotnet $argString"
    Info "Starting $Name..."
    Info "  stdout → $StdoutLog"
    Info "  stderr → $StderrLog"

    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList $argString `
        -WorkingDirectory $WorkingPath `
        -RedirectStandardOutput $StdoutLog `
        -RedirectStandardError  $StderrLog `
        -NoNewWindow `
        -PassThru

    Add-Content -Path $LauncherLogFile -Value "[$((Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))] $Name started PID $($process.Id)"
    Ok "$Name started (PID $($process.Id))."
    return $process
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

# Fixed Compose project name ensures the PostgreSQL volume persists across
# tester package upgrades even when installed to a different folder.
$env:COMPOSE_PROJECT_NAME = "birknext-studio-local"

Info "Repository root: $repoRoot"
Info "Backend path:    $backendPath"
Info "Frontend path:   $frontendPath"
Info "Runtime:         $ContainerRuntime"
Info "Mode:            $(if ($Fast) { 'Fast' } else { 'Safe' })"
Info "Container delay: $ContainerDelaySeconds seconds"
Info "Backend delay:   $BackendDelaySeconds seconds"
Info "Port cleanup:    $(if ($ForceKillPorts) { 'Force stale local dotnet processes' } else { 'Safe warning only' })"
Info "Compose Project: birknext-studio-local"
Info "Database Volume: birknext-studio-local_postgres_data"

# ── Log directory ─────────────────────────────────────────────────────────────
$logsDir         = Join-Path $repoRoot "logs"
$launcherLogFile = Join-Path $logsDir "launcher.log"
$backendOutLog   = Join-Path $logsDir "backend.out.log"
$backendErrLog   = Join-Path $logsDir "backend.err.log"
$frontendOutLog  = Join-Path $logsDir "frontend.out.log"
$frontendErrLog  = Join-Path $logsDir "frontend.err.log"

New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
Set-Content -Path $launcherLogFile -Value "BirkNext Launcher started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8

# Tell the backend to write Serilog rolling logs to the shared logs folder
$env:LoggingSettings__LogPath = $logsDir

Write-Host ""
Info "Logs folder:     $logsDir"
Info "Launcher log:    $launcherLogFile"
Info "Backend stdout:  $backendOutLog"
Info "Backend stderr:  $backendErrLog"
Info "Frontend stdout: $frontendOutLog"
Info "Frontend stderr: $frontendErrLog"
Write-Host ""

Require-Command "dotnet" "Install .NET SDK 8 or add dotnet to PATH."

if (-not (Test-Path $backendPath)) {
    throw "Backend folder not found: $backendPath"
}

if (-not (Test-Path $frontendPath)) {
    throw "Frontend folder not found: $frontendPath"
}

$backendRunnable = Find-DotNetRunnable -BasePath $backendPath -PreferredName "BirkNext.Api"

# Frontend: check for Blazor WASM artifact layout (wwwroot/index.html) before attempting dotnet runnable detection.
# BirkNext.Web is Blazor WebAssembly — its published output is static files, not a runnable server DLL.
$frontendWwwroot = Join-Path $frontendPath "wwwroot"
$frontendIndexHtml = Join-Path $frontendWwwroot "index.html"

if (Test-Path $frontendIndexHtml) {
    $frontendRunnable = [PSCustomObject]@{
        Kind = "StaticWeb"
        Path = $frontendWwwroot
    }
}
else {
    $frontendRunnable = Find-DotNetRunnable -BasePath $frontendPath -PreferredName "BirkNext.Web"

    if ($frontendRunnable.Kind -eq "Dll") {
        $frontendIndex = Get-ChildItem -Path $frontendPath -Recurse -Filter "index.html" -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\wwwroot\\index\.html$" -or $_.DirectoryName -eq $frontendPath } |
            Select-Object -First 1

        if ($frontendIndex) {
            $frontendRunnable = [PSCustomObject]@{
                Kind = "StaticWeb"
                Path = $frontendIndex.Directory.FullName
            }
        }
    }
}

Ok "Backend target:  $($backendRunnable.Path) [$($backendRunnable.Kind)]"
Ok "Frontend target: $($frontendRunnable.Path) [$($frontendRunnable.Kind)]"

$backendPort = if ($backendRunnable.Kind -eq "Project") { Get-ProjectLaunchPort -ProjectFile $backendRunnable.Path } else { $null }
$frontendPort = if ($frontendRunnable.Kind -eq "Project") { Get-ProjectLaunchPort -ProjectFile $frontendRunnable.Path } elseif ($frontendRunnable.Kind -eq "StaticWeb") { 5173 } else { $null }

if ($null -ne $backendPort) {
    Info "Backend port:    $backendPort"
}
else {
    Warn "Backend launch port could not be detected. Skipping backend port preflight."
}

if ($null -ne $frontendPort) {
    Info "Frontend port:   $frontendPort"
}
else {
    Warn "Frontend launch port could not be detected. Skipping frontend port preflight."
}

Push-Location $repoRoot

try {
    $canStart = $true

    if (-not $FrontendOnly -and $null -ne $backendPort) {
        $canStart = (Stop-PortOwnerIfAllowed -Name "Backend" -Port $backendPort -RepoRoot $repoRoot -ProjectFile $backendRunnable.Path) -and $canStart
    }

    if (-not $BackendOnly -and $null -ne $frontendPort) {
        $canStart = (Stop-PortOwnerIfAllowed -Name "Frontend" -Port $frontendPort -RepoRoot $repoRoot -ProjectFile $frontendRunnable.Path) -and $canStart
    }

    if (-not $canStart) {
        Warn "Startup stopped before launching backend/frontend because one or more required ports are busy."
        return
    }

    Start-ContainerServices

    $backendProcess  = $null
    $frontendProcess = $null

    if (-not $FrontendOnly) {
        $backendProcess = Start-DotNetProcessLogged `
            -Name "Backend" `
            -WorkingPath $backendPath `
            -Runnable $backendRunnable `
            -StdoutLog $backendOutLog `
            -StderrLog $backendErrLog `
            -LauncherLogFile $launcherLogFile

        if (-not $BackendOnly -and $BackendDelaySeconds -gt 0) {
            Info "Waiting $BackendDelaySeconds seconds for backend to initialize..."
            for ($i = 1; $i -le $BackendDelaySeconds; $i++) {
                Start-Sleep -Seconds 1
                if ($null -ne $backendProcess -and $backendProcess.HasExited) {
                    Fail "Backend process exited during startup (exit code: $($backendProcess.ExitCode))."
                    Warn "Check backend logs:"
                    Write-Host "  $backendErrLog"
                    Write-Host "  $backendOutLog"
                    Add-Content -Path $launcherLogFile -Value "[$((Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))] Backend exited early (code $($backendProcess.ExitCode))"
                    throw "Backend startup failed. See: $backendErrLog"
                }
            }
            Ok "Backend is still running."
        }
    }
    else {
        Warn "FrontendOnly selected. Backend will not be started."
    }

    if (-not $BackendOnly) {
        $frontendProcess = Start-DotNetProcessLogged `
            -Name "Frontend" `
            -WorkingPath $frontendPath `
            -Runnable $frontendRunnable `
            -StdoutLog $frontendOutLog `
            -StderrLog $frontendErrLog `
            -LauncherLogFile $launcherLogFile

        # Brief check — verify frontend didn't exit immediately
        Start-Sleep -Seconds 3
        if ($null -ne $frontendProcess -and $frontendProcess.HasExited) {
            Fail "Frontend process exited immediately (exit code: $($frontendProcess.ExitCode))."
            Warn "Check frontend logs:"
            Write-Host "  $frontendErrLog"
            Write-Host "  $frontendOutLog"
            Add-Content -Path $launcherLogFile -Value "[$((Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))] Frontend exited early (code $($frontendProcess.ExitCode))"
        }
    }
    else {
        Warn "BackendOnly selected. Frontend will not be started."
    }

    Write-Host ""
    Ok "Startup complete."
    Write-Host ""

    if (-not $FrontendOnly) {
        $backendUrl = if ($env:ASPNETCORE_URLS) { ($env:ASPNETCORE_URLS -split ";")[0] } else { "http://localhost:5000" }
        Write-Host "Backend:  $backendUrl" -ForegroundColor Green
    }

    if (-not $BackendOnly) {
        Write-Host "Frontend: http://localhost:5173" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "Log files:" -ForegroundColor Cyan
    Write-Host "  $launcherLogFile"
    if (-not $FrontendOnly) {
        Write-Host "  $backendOutLog"
        Write-Host "  $backendErrLog"
        Write-Host "  $(Join-Path $logsDir "backend-serilog-$(Get-Date -Format 'yyyyMMdd').log") (Serilog rolling)"
    }
    if (-not $BackendOnly) {
        Write-Host "  $frontendOutLog"
        Write-Host "  $frontendErrLog"
    }

    Write-Host ""
    Write-Host "Useful paths:" -ForegroundColor Cyan
    Write-Host "  /extract"
    Write-Host "  /scenarios"
}
catch {
    Fail $_.Exception.Message
    Write-Host ""
    if ((Test-Path Variable:launcherLogFile) -and (Test-Path $launcherLogFile)) {
        Warn "Launcher log: $launcherLogFile"
    }
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
