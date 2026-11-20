<#
.SYNOPSIS
  Runs BirkNext's browser workflow tests end to end on an isolated installation, in one command.

.DESCRIPTION
  1. Publishes the API and the web app (Release) from this working tree into an isolated folder.
  2. Creates a fresh, disposable PostgreSQL database (the API migrates it on start).
  3. Starts the API and the web app on their own free ports (never 5000/5173), the API as a local tester installation
     (Production + Local Tester Mode, loopback only).
  4. Runs the workflow tests (BirkNext.Web.PlaywrightTests, Tests.Workflows) against it. The real-data M2LB test runs first, on the empty
     database, when -M2lbArchive is given; the Performance Test Review test runs when -Performance is given (needs Podman with the k6 image);
     the Frontend Quality Review test runs when -FrontendQuality is given (needs Podman with node:20-bookworm and the ZAP image).
     The API gets its own appsettings.Local.json (every engine allowed, the frontend stand-in registered for ZAP); a developer's
     appsettings.Local.json is never copied.
  5. Stops everything it started and drops the database (unless -KeepDatabase), and reports the results. Exit code 0 = all passed.

  Nothing external is contacted: every test uses BirkNext and local stand-ins only. Your own BirkNext (5000/5173) and its database are not
  touched.

.EXAMPLE
  .\scripts\run-workflow-tests.ps1
.EXAMPLE
  .\scripts\run-workflow-tests.ps1 -M2lbArchive "$env:USERPROFILE\Downloads\M2LB (8).zip" -Performance -FrontendQuality
.EXAMPLE
  .\scripts\run-workflow-tests.ps1 -Filter "FullyQualifiedName~ApiQualityReviewWorkflowTests" -NoPublish
#>
param(
    [string]$OutDir = (Join-Path $env:TEMP 'birknext-workflow-tests'),
    [int]$ApiPort = 0,
    [int]$WebPort = 0,
    [string]$Database = ('birknext_wf_' + (Get-Date -Format 'yyyyMMdd_HHmmss')),
    [string]$PostgresContainer = 'birknext-studio-local_postgres_1',
    [string]$PostgresHost = 'localhost',
    [int]$PostgresPort = 5432,
    [string]$PostgresUser = 'birknext',
    [string]$PostgresPassword = 'birknext',
    [string]$M2lbArchive,
    [switch]$Performance,
    [switch]$FrontendQuality,
    [int]$FrontendTargetPort = 18092,
    [string]$Filter,
    [switch]$NoPublish,
    [switch]$KeepDatabase
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$ai = Join-Path $repo 'AIAssisted'
$apiDir = Join-Path $OutDir 'api'
$webDir = Join-Path $OutDir 'web'
$logs = Join-Path $OutDir 'logs'
$results = Join-Path $OutDir 'results'
New-Item -ItemType Directory -Force $OutDir, $logs, $results | Out-Null

function Write-Step([string]$text) { Write-Host "`n=== $text" -ForegroundColor Cyan }
function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop(); $port
}
function Test-PortFree([int]$port) {
    -not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

if ($ApiPort -eq 0) { $ApiPort = Get-FreePort }
if ($WebPort -eq 0) { do { $WebPort = Get-FreePort } while ($WebPort -eq $ApiPort) }
# Manual Testing translation: the isolated API uses the LibreTranslate provider pointed at a stand-in the browser tests serve on this port
# (in the test process; nothing external). Without the stand-in the provider reports the server unreachable.
do { $TranslatePort = Get-FreePort } while ($TranslatePort -in $ApiPort, $WebPort)
foreach ($p in $ApiPort, $WebPort, $TranslatePort) {
    if ($p -in 5000, 5173) { throw "Port $p is a developer port; the workflow tests never use 5000/5173." }
    if (-not (Test-PortFree $p)) { throw "Port $p is in use." }
}
if ($M2lbArchive -and -not (Test-Path $M2lbArchive)) { throw "M2LB archive not found: $M2lbArchive" }
foreach ($tool in 'dotnet', 'node') { if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is required." } }

$apiUrl = "http://localhost:$ApiPort"
$webUrl = "http://localhost:$WebPort"
$started = @()
# Removes a folder even when it holds paths longer than 260 characters (browser profiles, caches): robocopy mirrors an empty folder
# over it first, which works at any depth, then the empty shell is removed.
function Remove-Tree([string]$Path) {
    if (-not (Test-Path $Path)) { return }
    $empty = Join-Path ([IO.Path]::GetTempPath()) ('birknext-empty-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $empty | Out-Null
    robocopy $empty $Path /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    Remove-Item $empty -Force
    Remove-Item $Path -Recurse -Force
}

$standinStarted = $false
$fqrStandinStarted = $false
$databaseCreated = $false
$exitCode = 1
try {
    # ── 1. Publish ───────────────────────────────────────────────────────────────────────────────────────────────────────────
    if (-not $NoPublish) {
        Write-Step "Publishing API and web (Release) to $OutDir"
        $src = Join-Path $OutDir 'src'
        foreach ($d in $apiDir, $webDir, $src) { Remove-Tree $d }
        # Publish from a clean copy of the source (no bin/obj): an incremental publish from a working tree can pair an older
        # dotnet.native.wasm with a newer boot manifest, and the browser then refuses to start the app (integrity mismatch).
        foreach ($part in 'backend\BirkNext.Api', 'frontend\BirkNext.Web', 'shared', 'versioning', 'browser-companion') {
            robocopy (Join-Path $ai $part) (Join-Path $src "AIAssisted\$part") /E /XD bin obj node_modules TestResults .lighthouse-profile-* /XF appsettings.Local.json /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "Copying $part failed (robocopy $LASTEXITCODE)." }
        }
        $env:DOTNET_gcServer = '0'
        dotnet publish (Join-Path $src 'AIAssisted\backend\BirkNext.Api\BirkNext.Api.csproj') -c Release -o $apiDir --disable-build-servers -p:UseSharedCompilation=false -v:q -nologo *> (Join-Path $logs 'publish-api.log')
        if ($LASTEXITCODE -ne 0) { throw "API publish failed: see $logs\publish-api.log" }
        dotnet publish (Join-Path $src 'AIAssisted\frontend\BirkNext.Web\BirkNext.Web.csproj') -c Release -o $webDir --disable-build-servers -p:UseSharedCompilation=false -v:q -nologo *> (Join-Path $logs 'publish-web.log')
        if ($LASTEXITCODE -ne 0) { throw "Web publish failed: see $logs\publish-web.log" }
        Remove-Item env:DOTNET_gcServer -ErrorAction SilentlyContinue
        # The browser enforces the integrity of every framework file: check it before any test, so a broken publish fails here, clearly.
        & (Join-Path $PSScriptRoot 'check-blazor-integrity.ps1') -Wwwroot (Join-Path $webDir 'wwwroot') *> (Join-Path $logs 'integrity.log')
        if ($LASTEXITCODE -ne 0) { throw "The published web app would not start (integrity mismatch): see $logs\integrity.log" }
    }
    elseif (-not (Test-Path (Join-Path $apiDir 'BirkNext.Api.dll'))) { throw "-NoPublish: no earlier publish in $OutDir" }
    # A fresh start for the file-backed state too (Test Data, Critical E2E flows, credentials, sessions): -NoPublish used to keep the previous
    # run's App_Data, so a test met its own leftovers.
    Remove-Tree (Join-Path $apiDir 'App_Data')

    # The isolated API never reads a developer's appsettings.Local.json (it is not copied). Its own local settings allow every Frontend Quality
    # Review engine and register the frontend stand-in (and only it) for Passive Web Security (ZAP) as a loopback test target.
    $localSettings = [ordered]@{
        ManualTesting = [ordered]@{ Translation = [ordered]@{ Provider = 'LibreTranslate'; LibreTranslate = [ordered]@{ Url = "http://127.0.0.1:$TranslatePort" } } }
        FrontendQualityCapabilities = [ordered]@{ BrowserRuntimeAllowed = $true; AccessibilityAllowed = $true; LighthouseAllowed = $true; PassiveSecurityAllowed = $true }
        FrontendQualityEnginePreferences = [ordered]@{ BrowserRuntimeEnabled = $true; AccessibilityEnabled = $true; LighthouseEnabled = $true; PassiveSecurityEnabled = $true }
        FrontendPassiveSecurity = [ordered]@{
            Enabled = $true; ContainerRuntime = 'podman'
            TrustedProfiles = [ordered]@{ 'fqr-standin' = [ordered]@{ BaseUrl = "http://localhost:$FrontendTargetPort"; EnvironmentType = 'LoopbackLocalTest' } }
        }
    }
    [IO.File]::WriteAllText((Join-Path $apiDir 'appsettings.Local.json'), ($localSettings | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    # Lighthouse runs a pinned Node runner whose packages are not in the publish output: the installed ones are copied next to it.
    $lighthouseModules = Join-Path $ai 'backend\BirkNext.Api\Tools\LighthouseRunner\node_modules'
    if (Test-Path $lighthouseModules) {
        robocopy $lighthouseModules (Join-Path $apiDir 'Tools\LighthouseRunner\node_modules') /E /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copying the Lighthouse runner packages failed (robocopy $LASTEXITCODE)." }
    }
    else { Write-Host 'Lighthouse runner packages are not installed (npm ci in Tools\LighthouseRunner): Lighthouse will report itself unavailable.' }

    # The published web app must call this API, never the developer default (http://localhost:5000). Only the isolated copy is changed.
    $settings = Join-Path $webDir 'wwwroot\appsettings.json'
    $json = [IO.File]::ReadAllText($settings) -replace '"BackendUrl"\s*:\s*"[^"]*"', ('"BackendUrl": "' + $apiUrl + '"')
    [IO.File]::WriteAllText($settings, $json, [Text.UTF8Encoding]::new($false))
    Remove-Item "$settings.br", "$settings.gz" -ErrorAction SilentlyContinue

    # ── 2. Database ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    Write-Step "Fresh database $Database"
    if ($Database -match '^birknext$') { throw "Refusing the shared 'birknext' database; use a disposable name." }
    if (Get-Command podman -ErrorAction SilentlyContinue) {
        $exists = podman exec $PostgresContainer psql -U $PostgresUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$Database'" 2>$null
        if ($exists -eq '1') { throw "Database $Database already exists; choose another name." }
        podman exec $PostgresContainer psql -U $PostgresUser -d postgres -c "CREATE DATABASE $Database OWNER $PostgresUser;" | Out-Null
        if ($LASTEXITCODE -eq 0) { $databaseCreated = $true } else { Write-Host "Could not create it in container $PostgresContainer; the API creates it on start." }
    }

    # ── 3. Start API and web ─────────────────────────────────────────────────────────────────────────────────────────────────
    Write-Step "Starting API on $apiUrl and web on $webUrl"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $env:BirkNext__LocalTesterMode = 'true'
    $env:ASPNETCORE_URLS = $apiUrl
    $env:ConnectionStrings__Default = "Host=$PostgresHost;Port=$PostgresPort;Database=$Database;Username=$PostgresUser;Password=$PostgresPassword"
    $env:FRONTEND_ORIGIN = $webUrl
    # The isolated API keeps its file-backed state in its own folder even when the developer has redirected App_Data for their own BirkNext.
    $env:BIRKNEXT_APP_DATA_DIR = (Join-Path $apiDir 'App_Data')
    $started += Start-Process dotnet -ArgumentList 'BirkNext.Api.dll' -WorkingDirectory $apiDir -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logs 'api.log') -RedirectStandardError (Join-Path $logs 'api-error.log')
    foreach ($name in 'ASPNETCORE_ENVIRONMENT', 'BirkNext__LocalTesterMode', 'ASPNETCORE_URLS', 'ConnectionStrings__Default', 'FRONTEND_ORIGIN', 'BIRKNEXT_APP_DATA_DIR') { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
    $started += Start-Process node -ArgumentList "`"$(Join-Path $PSScriptRoot 'workflow-tests\serve-web.cjs')`"", "`"$(Join-Path $webDir 'wwwroot')`"", $WebPort -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logs 'web.log') -RedirectStandardError (Join-Path $logs 'web-error.log')
    $ready = $false
    for ($i = 0; $i -lt 120 -and -not $ready; $i++) {
        Start-Sleep -Seconds 2
        $status = & curl.exe -s -o NUL -w "%{http_code}" --max-time 3 "$apiUrl/api/projects"
        $ready = $status -in '200', '401', '403'
        if ($started[0].HasExited) { throw "The API stopped while starting: see $logs\api.log and api-error.log" }
    }
    if (-not $ready) { throw "The API did not answer within 4 minutes: see $logs\api.log" }
    $launchCode = Join-Path $apiDir 'App_Data\local-session\launch-code.txt'
    for ($i = 0; $i -lt 30 -and -not (Test-Path $launchCode); $i++) { Start-Sleep -Seconds 1 }
    if (-not (Test-Path $launchCode)) { throw "No local-session launch code at $launchCode (is Local Tester Mode on?)" }

    # ── 4. Tests ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
    Write-Step 'Building the workflow tests'
    Push-Location (Join-Path $ai 'frontend')
    # Own build output (artifacts path), never the working tree's bin/obj: a developer's running BirkNext.Api.exe (5000) locks
    # the API projects bin\Debug folder, and the test project references the API project.
    $testArtifacts = Join-Path $OutDir 'test-build'
    dotnet build BirkNext.Web.PlaywrightTests --artifacts-path $testArtifacts -v:q -nologo *> (Join-Path $logs 'build-tests.log')
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Test build failed: see $logs\build-tests.log" }

    $env:BIRKNEXT_REPOSITORY_ROOT = $repo
    $env:BIRKNEXT_PLAYWRIGHT_FRONTEND_URL = $webUrl
    $env:BIRKNEXT_PLAYWRIGHT_BACKEND_URL = $apiUrl
    $env:BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE = $launchCode
    $env:BIRKNEXT_PLAYWRIGHT_TRANSLATE_PORT = "$TranslatePort"
    if ($M2lbArchive) { $env:BIRKNEXT_M2LB_MANUAL_ARCHIVE = (Resolve-Path $M2lbArchive).Path }
    if ($Performance) {
        Write-Step 'Starting the performance stand-in target (Podman)'
        $target = & (Join-Path $ai 'frontend\BirkNext.Web.PlaywrightTests\performance-target\start-target.ps1')
        if ($LASTEXITCODE -ne 0 -or -not $target) { throw 'The performance stand-in did not start (Podman and node:20-bookworm needed).' }
        $standinStarted = $true
        $env:BIRKNEXT_PERFORMANCE_TARGET_URL = $target
    }
    if ($FrontendQuality) {
        Write-Step 'Starting the frontend stand-in website (Podman)'
        $fqrTarget = & (Join-Path $ai 'frontend\BirkNext.Web.PlaywrightTests\frontend-target\start-target.ps1') -Port $FrontendTargetPort
        if ($LASTEXITCODE -ne 0 -or -not $fqrTarget) { throw 'The frontend stand-in did not start (Podman and node:20-bookworm needed).' }
        $fqrStandinStarted = $true
        $env:BIRKNEXT_FQR_TARGET_URL = $fqrTarget
    }

    $runs = @()
    $workflows = 'FullyQualifiedName~BirkNext.Web.PlaywrightTests.Tests.Workflows'
    if ($Filter) { $runs += @{ Name = 'selected'; Filter = $Filter } }
    else {
        # The real-data test needs the empty database, so it runs first and alone.
        if ($M2lbArchive) { $runs += @{ Name = 'real-m2lb'; Filter = 'FullyQualifiedName~RealM2lbManualTestingTests' } }
        $runs += @{ Name = 'workflows'; Filter = "$workflows&FullyQualifiedName!~RealM2lbManualTestingTests" }
    }
    $failed = 0
    $summary = @()
    foreach ($run in $runs) {
        Write-Step "Tests: $($run.Name)"
        $trx = "$($run.Name).trx"
        # Windows PowerShell turns a native tool's stderr into error records; a failing test must be reported, never stop the script.
        $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $log = Join-Path $logs "tests-$($run.Name).log"
        & cmd.exe /c "dotnet test BirkNext.Web.PlaywrightTests --no-build --artifacts-path `"$testArtifacts`" --filter `"$($run.Filter)`" --logger `"trx;LogFileName=$trx`" --results-directory `"$results`" > `"$log`" 2>&1"
        $code = $LASTEXITCODE
        $ErrorActionPreference = $previous
        Select-String -Path $log -Pattern 'Passed!|Failed!|^\s+Failed |^\s+Skipped ' | ForEach-Object { Write-Host $_.Line }
        if ($code -ne 0) { $failed++; Write-Host "  details: $log" }
        $file = Join-Path $results $trx
        if (Test-Path $file) {
            [xml]$x = Get-Content $file -Raw
            foreach ($r in $x.TestRun.Results.UnitTestResult) { $summary += [pscustomobject]@{ Run = $run.Name; Outcome = $r.outcome; Test = ($r.testName -replace '^.*\.Tests\.Workflows\.', ''); Duration = $r.duration } }
        }
    }
    Pop-Location

    Write-Step 'Results'
    $summary | Sort-Object Outcome, Test | Format-Table Outcome, Duration, Test -AutoSize | Out-String -Width 220 | Write-Host
    $notRun = $summary | Where-Object Outcome -eq 'NotExecuted'
    if ($notRun) { Write-Host "Skipped tests name their missing prerequisite in $logs\tests-*.log (e.g. -M2lbArchive, -Performance)." }
    $exitCode = if ($failed -eq 0 -and -not ($summary | Where-Object Outcome -eq 'Failed')) { 0 } else { 1 }
    Write-Host ($(if ($exitCode -eq 0) { 'ALL WORKFLOW TESTS PASSED' } else { 'WORKFLOW TESTS FAILED' })) -ForegroundColor $(if ($exitCode -eq 0) { 'Green' } else { 'Red' })
    Write-Host "Logs: $logs   Results: $results"
}
finally {
    # ── 5. Stop everything this script started ───────────────────────────────────────────────────────────────────────────────
    Write-Step 'Stopping'
    foreach ($p in $started) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
    if ($standinStarted) { podman rm --force --ignore birknext-pt-standin | Out-Null }
    if ($fqrStandinStarted) { podman rm --force --ignore birknext-fqr-standin | Out-Null }
    foreach ($name in 'BIRKNEXT_PLAYWRIGHT_FRONTEND_URL', 'BIRKNEXT_PLAYWRIGHT_BACKEND_URL', 'BIRKNEXT_PLAYWRIGHT_LAUNCH_CODE_FILE', 'BIRKNEXT_PLAYWRIGHT_TRANSLATE_PORT', 'BIRKNEXT_M2LB_MANUAL_ARCHIVE', 'BIRKNEXT_PERFORMANCE_TARGET_URL', 'BIRKNEXT_FQR_TARGET_URL') { Remove-Item "env:$name" -ErrorAction SilentlyContinue }
    if ($databaseCreated -and -not $KeepDatabase) {
        Start-Sleep -Seconds 2
        podman exec $PostgresContainer psql -U $PostgresUser -d postgres -c "DROP DATABASE IF EXISTS $Database WITH (FORCE);" | Out-Null
        Write-Host "Database $Database dropped."
    }
    elseif ($databaseCreated) { Write-Host "Database $Database kept." }
}
exit $exitCode
