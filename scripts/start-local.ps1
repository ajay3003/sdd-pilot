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

function Start-DotNetProjectWindow {
    param(
        [string]$Name,
        [string]$WorkingPath,
        [object]$Runnable
    )

    if (-not (Test-Path $WorkingPath)) {
        throw "$Name folder not found: $WorkingPath"
    }

    if (-not $Runnable -or -not (Test-Path $Runnable.Path)) {
        throw "$Name runnable target not found: $($Runnable.Path)"
    }

    if ($Runnable.Kind -eq "StaticWeb") {
        # Blazor WebAssembly artifact mode.
        # BirkNext.Web is not a runnable server DLL. It is static content under wwwroot.
        $escapedRoot = Escape-PowerShellSingleQuotedString -Value $Runnable.Path

        $runCommand = @"
`$wwwroot = '$escapedRoot'
`$port = 5173
`$prefix = "http://localhost:`$port/"

if (`$null -ne (Get-Command 'dotnet-serve' -ErrorAction SilentlyContinue)) {
    Write-Host "Serving Blazor WebAssembly frontend with dotnet-serve..." -ForegroundColor Cyan
    Write-Host "Frontend URL: `$prefix" -ForegroundColor Green
    Write-Host ""
    Write-Host "Keep this window open while testing." -ForegroundColor Yellow
    dotnet-serve --directory "`$wwwroot" --port `$port
}
else {
    Write-Host "NOTE: dotnet-serve not installed. Using built-in static file server." -ForegroundColor Yellow
    Write-Host "To install dotnet-serve: dotnet tool install --global dotnet-serve" -ForegroundColor Yellow
    Write-Host ""

    function Get-MimeType([string]`$path) {
        switch ([System.IO.Path]::GetExtension(`$path).ToLowerInvariant()) {
            ".html" { "text/html"; break }
            ".htm"  { "text/html"; break }
            ".js"   { "application/javascript"; break }
            ".mjs"  { "application/javascript"; break }
            ".css"  { "text/css"; break }
            ".json" { "application/json"; break }
            ".wasm" { "application/wasm"; break }
            ".dll"  { "application/octet-stream"; break }
            ".dat"  { "application/octet-stream"; break }
            ".pdb"  { "application/octet-stream"; break }
            ".png"  { "image/png"; break }
            ".jpg"  { "image/jpeg"; break }
            ".jpeg" { "image/jpeg"; break }
            ".gif"  { "image/gif"; break }
            ".svg"  { "image/svg+xml"; break }
            ".ico"  { "image/x-icon"; break }
            ".woff" { "font/woff"; break }
            ".woff2"{ "font/woff2"; break }
            default { "application/octet-stream"; break }
        }
    }

    `$listener = [System.Net.HttpListener]::new()
    `$listener.Prefixes.Add(`$prefix)
    `$listener.Start()

    Write-Host "Serving Blazor WebAssembly frontend from: `$wwwroot" -ForegroundColor Cyan
    Write-Host "Frontend URL: `$prefix" -ForegroundColor Green
    Write-Host ""
    Write-Host "Keep this window open while testing." -ForegroundColor Yellow

    Start-Process `$prefix

    while (`$listener.IsListening) {
        `$context = `$listener.GetContext()
        `$requestPath = [System.Uri]::UnescapeDataString(`$context.Request.Url.AbsolutePath.TrimStart('/'))

        if ([string]::IsNullOrWhiteSpace(`$requestPath)) {
            `$requestPath = "index.html"
        }

        `$candidate = Join-Path `$wwwroot `$requestPath

        if ((Test-Path `$candidate -PathType Container)) {
            `$candidate = Join-Path `$candidate "index.html"
        }

        if (-not (Test-Path `$candidate -PathType Leaf)) {
            `$candidate = Join-Path `$wwwroot "index.html"
        }

        try {
            `$bytes = [System.IO.File]::ReadAllBytes(`$candidate)
            `$context.Response.StatusCode = 200
            `$context.Response.ContentType = Get-MimeType `$candidate
            `$context.Response.ContentLength64 = `$bytes.Length
            `$context.Response.OutputStream.Write(`$bytes, 0, `$bytes.Length)
        }
        catch {
            `$message = [System.Text.Encoding]::UTF8.GetBytes(`$_.Exception.Message)
            `$context.Response.StatusCode = 500
            `$context.Response.ContentType = "text/plain"
            `$context.Response.OutputStream.Write(`$message, 0, `$message.Length)
        }
        finally {
            `$context.Response.OutputStream.Close()
        }
    }
}
"@
    }
    elseif ($Runnable.Kind -eq "Dll") {
        # Downloaded pipeline artifact mode: run the published backend DLL directly.
        $runCommand = "dotnet `"$($Runnable.Path)`""
    }
    elseif ($Fast) {
        # Source repository mode, fast startup.
        $runCommand = "dotnet run --no-build --project `"$($Runnable.Path)`""
    }
    else {
        # Source repository mode, safe startup.
        $runCommand = "dotnet restore `"$($Runnable.Path)`"; if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }; dotnet build `"$($Runnable.Path)`"; if (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }; dotnet run --project `"$($Runnable.Path)`""
    }

    $environmentCommand = ""
    if ($Name -eq "Backend" -and -not [string]::IsNullOrWhiteSpace($script:BackendConnectionString)) {
        $escapedConnectionString = Escape-PowerShellSingleQuotedString -Value $script:BackendConnectionString
        $escapedPostgresDb = Escape-PowerShellSingleQuotedString -Value $script:PostgresConfig.Database
        $escapedPostgresUser = Escape-PowerShellSingleQuotedString -Value $script:PostgresConfig.User
        $escapedPostgresPassword = Escape-PowerShellSingleQuotedString -Value $script:PostgresConfig.Password
        $environmentCommand = @"
`$env:ConnectionStrings__Default = '$escapedConnectionString'
`$env:POSTGRES_DB = '$escapedPostgresDb'
`$env:POSTGRES_USER = '$escapedPostgresUser'
`$env:POSTGRES_PASSWORD = '$escapedPostgresPassword'
Write-Host 'Database: $(Get-SanitizedConnectionString -PostgresConfig $script:PostgresConfig)' -ForegroundColor DarkGray
"@
    }

    $command = @"
`$host.UI.RawUI.WindowTitle = 'BirkNext $Name'
Set-Location '$WorkingPath'
Write-Host 'Starting BirkNext $Name...' -ForegroundColor Cyan
Write-Host 'Target: $($Runnable.Path)' -ForegroundColor DarkGray
Write-Host 'Mode: $($Runnable.Kind)' -ForegroundColor DarkGray
Write-Host ''
$environmentCommand
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
Info "Port cleanup:    $(if ($ForceKillPorts) { 'Force stale local dotnet processes' } else { 'Safe warning only' })"

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

    if (-not $FrontendOnly) {
        Start-DotNetProjectWindow -Name "Backend" -WorkingPath $backendPath -Runnable $backendRunnable

        if (-not $BackendOnly -and $BackendDelaySeconds -gt 0) {
            Info "Waiting $BackendDelaySeconds seconds before starting frontend..."
            Start-Sleep -Seconds $BackendDelaySeconds
        }
    }
    else {
        Warn "FrontendOnly selected. Backend will not be started."
    }

    if (-not $BackendOnly) {
        Start-DotNetProjectWindow -Name "Frontend" -WorkingPath $frontendPath -Runnable $frontendRunnable
    }
    else {
        Warn "BackendOnly selected. Frontend will not be started."
    }

    Write-Host ""
    Ok "Startup triggered."
    Write-Host ""

    if (-not $FrontendOnly) {
        $backendUrl = if ($env:ASPNETCORE_URLS) { ($env:ASPNETCORE_URLS -split ";")[0] } else { "http://localhost:5000" }
        Write-Host "Backend:  $backendUrl" -ForegroundColor Green
    }

    if (-not $BackendOnly) {
        Write-Host "Frontend: http://localhost:5173" -ForegroundColor Green
    }

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
