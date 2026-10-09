param(
    # Dataset id from KnownRealProjectDatasets (e.g. M2LB).
    [string]$Dataset = "M2LB",
    # Path to the EXTERNAL archive. Never copy it into the repository. Falls back to the dataset's own environment variable.
    [string]$Archive = "",
    [ValidateSet("Smoke", "Standard", "Full")]
    [string]$Mode = "Standard",
    [int]$BackendPort = 5091,
    [int]$FrontendPort = 5191,
    # Postgres (local container) used for a throwaway acceptance database.
    [string]$PostgresContainer = "aiassisted_postgres_1",
    [string]$PostgresUser = "birknext",
    [string]$PostgresPassword = "birknext",
    [switch]$NoBuild,
    [switch]$KeepDatabase
)

# Real Project Acceptance: imports one EXTERNAL project archive through the production Project Import flow into a fresh, isolated
# backend database and exercises every BirkNext page against that one workspace (see docs/real-project-acceptance.md).
# Nothing is uploaded anywhere else, no runtime target is contacted, no event is sent. Reports land in artifacts/real-project-acceptance
# (gitignored). Without an archive the test is skipped with "External real-project dataset not configured.".

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$backendDir = Join-Path $root "backend\BirkNext.Api"
$frontendDir = Join-Path $root "frontend\BirkNext.Web"
$testProject = Join-Path $root "frontend\BirkNext.Web.PlaywrightTests\BirkNext.Web.PlaywrightTests.csproj"
$database = "birknext_rpa_" + ([Guid]::NewGuid().ToString("N").Substring(0, 8))

if ($Archive -ne "") {
    if (-not (Test-Path $Archive)) { Write-Host "Archive not found: $Archive" -ForegroundColor Red; exit 2 }
    $Archive = (Resolve-Path $Archive).Path
}

if (-not $NoBuild) {
    & dotnet build (Join-Path $backendDir "BirkNext.Api.csproj") -nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet build (Join-Path $frontendDir "BirkNext.Web.csproj") -nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet build $testProject -nologo -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$saved = @{}
$names = "BIRKNEXT_REAL_ACCEPTANCE_DATASET", "BIRKNEXT_REAL_ACCEPTANCE_ARCHIVE", "BIRKNEXT_REAL_ACCEPTANCE_MODE", "BIRKNEXT_ACCEPTANCE_FRONTEND_URL",
    "BIRKNEXT_ACCEPTANCE_BACKEND_URL", "BIRKNEXT_ACCEPTANCE_FRONTEND_BACKEND_URL", "BIRKNEXT_ACCEPTANCE_COMMIT"
foreach ($n in $names) { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$backend = $null
$frontend = $null
try {
    & podman exec $PostgresContainer psql -U $PostgresUser -d postgres -c "CREATE DATABASE $database OWNER $PostgresUser;" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "Could not create the acceptance database in container $PostgresContainer." -ForegroundColor Red; exit 2 }

    # Backend: fresh database; diagnostics read the same external archive.
    $backendEnv = @{
        "ASPNETCORE_URLS" = "http://localhost:$BackendPort"
        "ASPNETCORE_ENVIRONMENT" = "Development"
        "FRONTEND_ORIGIN" = "http://localhost:$FrontendPort"
        "ConnectionStrings__Default" = "Host=localhost;Port=5432;Database=$database;Username=$PostgresUser;Password=$PostgresPassword"
        "ProjectCompatibility__AcceptanceArchivePath" = $Archive
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo "dotnet", "bin\Debug\net8.0\BirkNext.Api.dll"
    $psi.WorkingDirectory = $backendDir
    $psi.UseShellExecute = $false
    foreach ($k in $backendEnv.Keys) { $psi.Environment[$k] = $backendEnv[$k] }
    $backend = [System.Diagnostics.Process]::Start($psi)

    $frontend = Start-Process dotnet -ArgumentList "run", "--no-build", "--urls", "http://localhost:$FrontendPort" -WorkingDirectory $frontendDir -PassThru -WindowStyle Hidden

    foreach ($url in "http://localhost:$BackendPort/api/source-analysis", "http://localhost:$FrontendPort/") {
        $ready = $false
        for ($i = 0; $i -lt 120 -and -not $ready; $i++) {
            try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 5 | Out-Null; $ready = $true } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) { Write-Host "Server did not start: $url" -ForegroundColor Red; exit 2 }
    }

    $env:BIRKNEXT_REAL_ACCEPTANCE_DATASET = $Dataset
    if ($Archive -ne "") { $env:BIRKNEXT_REAL_ACCEPTANCE_ARCHIVE = $Archive }
    $env:BIRKNEXT_REAL_ACCEPTANCE_MODE = $Mode
    $env:BIRKNEXT_ACCEPTANCE_FRONTEND_URL = "http://localhost:$FrontendPort"
    $env:BIRKNEXT_ACCEPTANCE_BACKEND_URL = "http://localhost:$BackendPort"
    $env:BIRKNEXT_ACCEPTANCE_FRONTEND_BACKEND_URL = (Get-Content (Join-Path $frontendDir "wwwroot\appsettings.json") -Raw | ConvertFrom-Json).BackendUrl
    $env:BIRKNEXT_ACCEPTANCE_COMMIT = (& git -C $root rev-parse --short HEAD)

    & dotnet test $testProject --no-build --filter "Category=RealProjectAcceptance" --logger "console;verbosity=normal"
    exit $LASTEXITCODE
}
finally {
    foreach ($n in $names) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
    if ($frontend -and -not $frontend.HasExited) { & taskkill /T /F /PID $frontend.Id | Out-Null }
    if ($backend -and -not $backend.HasExited) { & taskkill /T /F /PID $backend.Id | Out-Null }
    if (-not $KeepDatabase) {
        Start-Sleep -Seconds 2
        & podman exec $PostgresContainer psql -U $PostgresUser -d postgres -c "DROP DATABASE IF EXISTS $database WITH (FORCE);" | Out-Null
    }
}
